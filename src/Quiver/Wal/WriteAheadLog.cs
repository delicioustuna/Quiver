using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Threading.Channels;
using Quiver.Core;
using Quiver.Core.Telemetry;

namespace Quiver.Wal;

public sealed class WriteAheadLog : IWriteAheadLog
{
    // ヘッダレイアウト: Length(4) + Lsn(8) + TxId(8) + Type(1) + Crc32C(4) = 25 バイト
    internal const int HeaderSize = 25;
    private const long DefaultSegmentCapacity = 64L * 1024 * 1024;
    private const int WriteBufferSize = 1024 * 1024;
    internal const int MaxPayloadSize = 8 * 1024 * 1024;

    private readonly string _directory;
    private readonly long _segmentCapacity;
    private readonly object _writeLock = new();
    private readonly Channel<FlushRequest> _flushChannel;
    private readonly Task _flushTask;
    // FT-27: group commit window を Stopwatch tick に変換して保持 (0 = 無効)。
    // sub-millisecond 精度の spin-wait に Stopwatch.GetTimestamp を使う。
    private readonly long _groupCommitWindowTicks;
    // FT-27: 観測用カウンタ (テスト・診断用)。
    // FlushBatchCount = fsync が走った回数、FlushRequestCount = FlushTo を呼んだ回数。
    // 両者の比 (FlushRequestCount / FlushBatchCount) が平均バッチサイズになる。
    private long _flushBatchCount;
    private long _flushRequestCount;

    // FT-29: 複数 tx の PageImage を Commit/CheckpointBegin/CheckpointEnd の直前にまとめて
    // drain する共有 coalesce バッファ。`(fileKind, pageId)` ごとに「最後に書いた tx」の
    // payload を 1 件だけ保持し、同一ページに対する重複 PageImage 出力を抑制する。
    // 並行 tx が異なるページを同時にコミットすると drain 段階で 1 バッチに集約され、
    // ロック回数も減るため WAL 書き込みオーバヘッドが下がる。
    // 同一ページに対する書き込みは PagedFile のフレーム X-lock により直列化されるが、
    // 「Tx_A の FlushPending 〜 Append(Commit_A)」の窓に Tx_B が同一ページを上書きする
    // 経路は存在しうるため、latest-wins de-dup が必要。
    private readonly Dictionary<(byte FileKind, long PageId), CoalescedPageImage> _coalescedPageImages = new();
    private long _coalescedPageImageCount;
    private long _drainedPageImageCount;

    private long _nextLsn;
    private long _flushedLsn = -1;
    private long _bytesWritten;
    private long _currentSegIdx;
    private long _segBytesUsed;
    private FileStream? _segStream;
    private readonly byte[] _buffer = new byte[WriteBufferSize];
    private int _bufPos;
    private bool _disposed;

    // segIdx → first LSN of that segment (rebuilt on open, updated on segment roll)
    private readonly SortedDictionary<long, long> _segFirstLsn = new();

    public long CurrentLsn => Volatile.Read(ref _nextLsn) - 1;
    public long FlushedLsn => Volatile.Read(ref _flushedLsn);
    public long BytesWritten => Volatile.Read(ref _bytesWritten);

    /// <summary>
    /// FT-27: バックグラウンドフラッシュループが実際に fsync を起動した回数。
    /// テスト・診断用。
    /// </summary>
    public long FlushBatchCount => Volatile.Read(ref _flushBatchCount);

    /// <summary>
    /// FT-27: <see cref="FlushTo"/> 経由でフラッシュ要求された累計回数。
    /// <c>FlushRequestCount / FlushBatchCount</c> が平均グループサイズ。
    /// </summary>
    public long FlushRequestCount => Volatile.Read(ref _flushRequestCount);

    /// <summary>
    /// FT-29: 同一 (fileKind, pageId) が coalesce バッファで上書きされた回数 (cross-tx
    /// de-dup ヒット数)。<c>BufferPageImage</c> が既存エントリを置き換えた件数の累計。
    /// </summary>
    public long CoalescedPageImageCount => Volatile.Read(ref _coalescedPageImageCount);

    /// <summary>
    /// FT-29: coalesce バッファから drain されて WAL レコード化された PageImage 件数の累計。
    /// </summary>
    public long DrainedPageImageCount => Volatile.Read(ref _drainedPageImageCount);

    public WriteAheadLog(string directory, long segmentCapacity = DefaultSegmentCapacity)
        : this(directory, segmentCapacity, TimeSpan.Zero) { }

    public WriteAheadLog(string directory, long segmentCapacity, TimeSpan groupCommitWindow)
    {
        _directory = directory;
        _segmentCapacity = segmentCapacity;
        _groupCommitWindowTicks = groupCommitWindow > TimeSpan.Zero
            ? (long)(groupCommitWindow.TotalSeconds * Stopwatch.Frequency)
            : 0;
        Directory.CreateDirectory(directory);
        _flushChannel = Channel.CreateUnbounded<FlushRequest>(
            new UnboundedChannelOptions { SingleReader = true });
        RebuildState();
        _segStream = OpenSegStream(_currentSegIdx, append: true);
        _segBytesUsed = _segStream.Position;
        _flushTask = Task.Factory.StartNew(
            RunFlushLoopAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public long Append(WalRecordType type, TransactionId tx, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new StorageException($"WAL payload size {payload.Length} exceeds max {MaxPayloadSize}");

        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // FT-29: Commit / Checkpoint sentinel の直前で coalesce バッファを drain。
            // PageImage LSN < Commit/CheckpointBegin/End LSN の不変条件を保ち、
            // recovery が PageImage を Commit より先に観測できることを保証する。
            // (torn-Commit heuristic は「PageImage が durable で Commit が無い」場合を
            // committed 扱いするため、PageImage は必ず Commit より先に LSN を取る。)
            if (type == WalRecordType.Commit ||
                type == WalRecordType.CheckpointBegin ||
                type == WalRecordType.CheckpointEnd ||
                type == WalRecordType.Checkpoint ||
                type == WalRecordType.Abort)
            {
                DrainCoalesceBufferLocked();
            }

            return WriteRecordLocked(type, tx.Value, payload);
        }
    }

    private long WriteRecordLocked(WalRecordType type, long txIdValue, ReadOnlySpan<byte> payload)
    {
        int recordSize = HeaderSize + payload.Length;

        if (_segBytesUsed + _bufPos + recordSize > _segmentCapacity)
        {
            WriteMarkerLocked(WalRecordType.EndOfSegment);
            FlushBufferLocked();
            RollSegmentLocked();
        }
        else if (_bufPos + recordSize > WriteBufferSize)
        {
            FlushBufferLocked();
        }

        long lsn = _nextLsn++;
        _segFirstLsn.TryAdd(_currentSegIdx, lsn);
        WriteRecordToBuffer(lsn, type, txIdValue, payload);
        _bytesWritten += recordSize;
        QuiverTelemetry.WalBytesWritten.Add(recordSize);
        QuiverEventSource.Log.WalBytesWritten(recordSize);
        return lsn;
    }

    /// <summary>
    /// FT-29: PageImage を共有 coalesce バッファへ投入する。
    ///
    /// 同一 tx の同一 `(fileKind, pageId)` は latest-wins で de-dup (intra-tx coalesce)。
    /// 異なる tx が同一キーで来た場合は <b>既存エントリを先に drain</b> してから新エントリを
    /// 入れる (cross-tx は coalesce しない)。これにより 1 PageImage レコードは必ず単一の
    /// "writer tx" にしか紐付かず、Pass 3 undo の CLR が別 tx の committed content を
    /// 巻き戻す危険を完全に排除する (= correctness 優先設計)。
    ///
    /// 実際の WAL 追記は次の <see cref="WalRecordType.Commit"/> /
    /// <see cref="WalRecordType.CheckpointBegin"/> / <see cref="WalRecordType.CheckpointEnd"/> /
    /// <see cref="WalRecordType.Abort"/> 出力時に <see cref="DrainCoalesceBufferLocked"/> で
    /// まとめて行う。
    /// </summary>
    public void BufferPageImage(TransactionId tx, byte fileKind, long pageId, byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (payload.Length > MaxPayloadSize)
            throw new StorageException($"WAL payload size {payload.Length} exceeds max {MaxPayloadSize}");

        var key = (fileKind, pageId);
        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_coalescedPageImages.TryGetValue(key, out var existing))
            {
                if (existing.Tx.Value != tx.Value)
                {
                    // 別 tx が同一ページに書こうとした: 既存エントリを drain して per-tx 帰属を
                    // 保持する (cross-tx coalesce は意図的に行わない — recovery undo の安全性のため)。
                    WriteRecordLocked(WalRecordType.PageImage, existing.Tx.Value, existing.Payload);
                    Interlocked.Increment(ref _drainedPageImageCount);
                }
                else
                {
                    // 同一 tx の同一ページに対する再書き込み = intra-tx coalesce hit。
                    Interlocked.Increment(ref _coalescedPageImageCount);
                }
            }
            _coalescedPageImages[key] = new CoalescedPageImage(tx, payload);
        }
    }

    /// <summary>
    /// FT-29: <paramref name="tx"/> が coalesce バッファに残しているエントリをすべて除去する。
    /// abort 経路から呼ばれ、ロールバックされた tx の PageImage が後続の drain で WAL へ漏れるのを防ぐ。
    /// </summary>
    public void EvictCoalescedPageImagesFor(TransactionId tx)
    {
        lock (_writeLock)
        {
            if (_disposed || _coalescedPageImages.Count == 0) return;
            List<(byte, long)>? remove = null;
            foreach (var kv in _coalescedPageImages)
            {
                if (kv.Value.Tx.Value == tx.Value)
                    (remove ??= new()).Add(kv.Key);
            }
            if (remove == null) return;
            foreach (var key in remove)
                _coalescedPageImages.Remove(key);
        }
    }

    private void DrainCoalesceBufferLocked()
    {
        if (_coalescedPageImages.Count == 0) return;
        foreach (var entry in _coalescedPageImages.Values)
        {
            WriteRecordLocked(WalRecordType.PageImage, entry.Tx.Value, entry.Payload);
            Interlocked.Increment(ref _drainedPageImageCount);
        }
        _coalescedPageImages.Clear();
    }

    private readonly record struct CoalescedPageImage(TransactionId Tx, byte[] Payload);

    public void FlushTo(long lsn)
    {
        if (Volatile.Read(ref _flushedLsn) >= lsn) return;
        Interlocked.Increment(ref _flushRequestCount);
        // OB-1: WAL flush span + duration histogram。
        using var activity = QuiverTelemetry.WalFlushActivitySource.StartActivity(
            "wal.flush", ActivityKind.Internal);
        activity?.SetTag("quiver.wal.target_lsn", lsn);
        var sw = Stopwatch.StartNew();
        // OB-2: dotnet-counters の wal-pending-flush-count gauge。fsync 完了で decrement。
        QuiverEventSource.Log.WalFlushRequestStarted();
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_flushChannel.Writer.TryWrite(new FlushRequest(lsn, tcs)))
            {
                // チャネルが完了 (Dispose 済み) — 念のためもう一度確認する
                if (Volatile.Read(ref _flushedLsn) >= lsn) return;
                throw new ObjectDisposedException(nameof(WriteAheadLog));
            }
            tcs.Task.GetAwaiter().GetResult();
            QuiverTelemetry.WalFlushDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            // OB-3: Trace レベルで残す。bytes/sec のメトリクスは別経路で取れるので、
            // ここはトラブル時に有効化する用途を想定して Trace 止まり (allocation はソース生成で抑制)。
            QuiverLog.WalFlushed(QuiverLog.WalLogger, lsn, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            QuiverEventSource.Log.WalFlushRequestCompleted();
        }
    }

    public long WriteCheckpoint(long oldestActiveLsn, long lastFlushedDataLsn)
    {
        Span<byte> payload = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(payload, oldestActiveLsn);
        BinaryPrimitives.WriteInt64LittleEndian(payload[8..], lastFlushedDataLsn);
        long lsn = Append(WalRecordType.Checkpoint, new TransactionId(-1), payload);
        FlushTo(lsn);
        return lsn;
    }

    public long WriteCheckpointBegin(long oldestActiveLsn, int dirtyPageCount)
    {
        // ペイロード: oldestActiveLsn(8) + dirtyPageCount(4) + timestampTicks(8) = 20 バイト
        Span<byte> payload = stackalloc byte[20];
        BinaryPrimitives.WriteInt64LittleEndian(payload, oldestActiveLsn);
        BinaryPrimitives.WriteInt32LittleEndian(payload[8..], dirtyPageCount);
        BinaryPrimitives.WriteInt64LittleEndian(payload[12..], DateTime.UtcNow.Ticks);
        long lsn = Append(WalRecordType.CheckpointBegin, new TransactionId(-1), payload);
        FlushTo(lsn);
        return lsn;
    }

    public long WriteCheckpointEnd(long beginLsn)
    {
        // ペイロード: beginLsn(8) = 8 バイト
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, beginLsn);
        long lsn = Append(WalRecordType.CheckpointEnd, new TransactionId(-1), payload);
        FlushTo(lsn);
        return lsn;
    }

    public long WriteFileTruncate(byte fileKind, long newPageCount)
    {
        if (newPageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(newPageCount));
        // ペイロード: fileKind(1) + newPageCount(8) = 9 バイト
        Span<byte> payload = stackalloc byte[9];
        payload[0] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload[1..], newPageCount);
        long lsn = Append(WalRecordType.FileTruncate, new TransactionId(-1), payload);
        FlushTo(lsn);
        return lsn;
    }

    public void Truncate(long uptoLsn)
    {
        lock (_writeLock)
        {
            var keys = _segFirstLsn.Keys.ToList();
            for (int i = 0; i < keys.Count - 1; i++)
            {
                long segIdx = keys[i];
                if (segIdx == _currentSegIdx) continue;
                long nextFirst = _segFirstLsn[keys[i + 1]];
                // segIdx 内の全レコードは LSN < nextFirst を満たすので、nextFirst-1 <= uptoLsn なら安全に削除可能
                if (nextFirst - 1 <= uptoLsn)
                {
                    try { File.Delete(SegmentPath(segIdx)); } catch { }
                    _segFirstLsn.Remove(segIdx);
                }
            }
        }
    }

    public IWalReader OpenReader(long startLsn)
    {
        long[] segIndices;
        lock (_writeLock)
        {
            // firstLsn <= startLsn を満たす最後のセグメントを探す。
            // どのセグメントも条件を満たさない (startLsn が現存する最古セグメントより
            // 前 — Truncate 済み) 場合は、利用可能な最古セグメントから読み始める。
            // 既定を _currentSegIdx にすると Truncate 後の recovery が
            // 過去セグメントの Checkpoint レコードを取りこぼすため。
            long startSeg = _segFirstLsn.Count > 0 ? _segFirstLsn.Keys.First() : _currentSegIdx;
            foreach (var (segIdx, firstLsn) in _segFirstLsn)
            {
                if (firstLsn <= startLsn) startSeg = segIdx;
                else break;
            }
            segIndices = [.. _segFirstLsn.Keys.Where(k => k >= startSeg).OrderBy(k => k)];
        }
        return new WalReader(_directory, segIndices, startLsn);
    }

    public void Dispose()
    {
        if (_disposed) return;
        // FT-29: Dispose 完了前に coalesce バッファを最終 drain する。
        // 残っているのは「FlushPending したが Commit/Abort まだ」の窓で Dispose された
        // ケース (typical には正常 shutdown の最終 checkpoint で空になっているはず) で、
        // recovery 側で「PageImage あり Commit 無し」= 正常な torn-Commit 経路として扱われる。
        lock (_writeLock)
        {
            DrainCoalesceBufferLocked();
        }
        _disposed = true;
        _flushChannel.Writer.TryComplete();
        try { _flushTask.GetAwaiter().GetResult(); } catch { }
        lock (_writeLock)
        {
            FlushBufferLocked();
            _segStream?.Flush(flushToDisk: true);
            _segStream?.Dispose();
            _segStream = null;
        }
    }

    // -----------------------------------------------------------------------
    // 書き込みヘルパ (すべて _writeLock の下で呼ばれる)
    // -----------------------------------------------------------------------

    private void WriteRecordToBuffer(long lsn, WalRecordType type, long txId, ReadOnlySpan<byte> payload)
    {
        int recordSize = HeaderSize + payload.Length;
        Span<byte> dest = _buffer.AsSpan(_bufPos, recordSize);

        BinaryPrimitives.WriteInt32LittleEndian(dest, recordSize);
        BinaryPrimitives.WriteInt64LittleEndian(dest[4..], lsn);
        BinaryPrimitives.WriteInt64LittleEndian(dest[12..], txId);
        dest[20] = (byte)type;
        if (payload.Length > 0) payload.CopyTo(dest[HeaderSize..]);

        // CRC32 over bytes[0..21) (header without checksum field) + payload
        var crc = new Crc32();
        crc.Append(dest[..21]);
        if (payload.Length > 0) crc.Append(dest[HeaderSize..]);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[21..], crc.GetCurrentHashAsUInt32());

        _bufPos += recordSize;
    }

    private void WriteMarkerLocked(WalRecordType type)
    {
        if (_bufPos + HeaderSize > WriteBufferSize)
            FlushBufferLocked();
        // Marker: lsn=-1, txId=-1, no payload
        WriteRecordToBuffer(-1L, type, -1L, ReadOnlySpan<byte>.Empty);
    }

    private void FlushBufferLocked()
    {
        if (_bufPos == 0 || _segStream == null) return;
        _segStream.Write(_buffer, 0, _bufPos);
        _segBytesUsed += _bufPos;
        _bufPos = 0;
    }

    private void RollSegmentLocked()
    {
        _segStream?.Dispose();
        _currentSegIdx++;
        _segBytesUsed = 0;
        _segStream = OpenSegStream(_currentSegIdx, append: false);
    }

    // -----------------------------------------------------------------------
    // フラッシュループ (バックグラウンドタスク — グループコミット)
    // -----------------------------------------------------------------------

    private async Task RunFlushLoopAsync()
    {
        var pending = new List<FlushRequest>();
        while (await _flushChannel.Reader.WaitToReadAsync())
        {
            // FT-27: group commit window — 最初の要求が来た時点でこの window 経過まで
            // spin-wait し、追加で積まれた要求も同じ fsync で処理する。Windows の
            // Task.Delay は ~15ms 解像度なので Stopwatch + Thread.SpinWait で sub-ms 精度を
            // 確保する。本タスクは LongRunning 専用スレッドで動くため busy-wait しても
            // 他のワークを阻害しない。Dispose と同時に Channel が完了するので、最後の
            // 周回も window を待ってから drain → fsync して整合性を保つ。
            if (_groupCommitWindowTicks > 0 && !_disposed)
            {
                long deadline = Stopwatch.GetTimestamp() + _groupCommitWindowTicks;
                while (Stopwatch.GetTimestamp() < deadline && !_disposed)
                    Thread.SpinWait(50);
            }

            while (_flushChannel.Reader.TryRead(out var req))
                pending.Add(req);

            long highestLsn;
            lock (_writeLock)
            {
                FlushBufferLocked();
                _segStream?.Flush(flushToDisk: true);
                highestLsn = _nextLsn > 0 ? _nextLsn - 1 : -1;
                Volatile.Write(ref _flushedLsn, highestLsn);
            }
            // バッチサイズに関わらず fsync 1 回ごとに 1 カウントする
            // (pending が空でも WaitToReadAsync は要求があったから戻った)。
            Interlocked.Increment(ref _flushBatchCount);

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].TargetLsn <= highestLsn)
                {
                    pending[i].Tcs.TrySetResult(true);
                    pending.RemoveAt(i);
                }
            }
        }

        foreach (var req in pending)
            req.Tcs.TrySetCanceled();
    }

    // -----------------------------------------------------------------------
    // セグメント関連ヘルパ
    // -----------------------------------------------------------------------

    private FileStream OpenSegStream(long segIdx, bool append)
    {
        string path = SegmentPath(segIdx);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (append) stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    internal string SegmentPath(long segIdx) =>
        Path.Combine(_directory, $"wal.{segIdx:D8}.log");

    private void RebuildState()
    {
        _segFirstLsn.Clear();
        var segIndices = Directory.GetFiles(_directory, "wal.????????.log")
            .Select(f => long.Parse(Path.GetFileNameWithoutExtension(f)[4..]))
            .OrderBy(x => x)
            .ToList();

        if (segIndices.Count == 0)
        {
            _currentSegIdx = 0;
            _nextLsn = 0;
            return;
        }

        long maxLsn = -1;
        foreach (long segIdx in segIndices)
        {
            long firstLsn = -1;
            try
            {
                using var fs = new FileStream(SegmentPath(segIdx),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                while (WalReader.TryReadRecord(fs, out WalRecord rec))
                {
                    if (rec.Type == WalRecordType.EndOfSegment) break;
                    if (firstLsn < 0) firstLsn = rec.Lsn;
                    if (rec.Lsn > maxLsn) maxLsn = rec.Lsn;
                }
            }
            catch { }

            if (firstLsn >= 0) _segFirstLsn[segIdx] = firstLsn;
            _currentSegIdx = segIdx;
        }

        _nextLsn = maxLsn >= 0 ? maxLsn + 1 : 0;
    }

    private readonly record struct FlushRequest(long TargetLsn, TaskCompletionSource<bool> Tcs);
}

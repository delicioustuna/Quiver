using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Threading.Channels;
using Quiver.Core;
using Quiver.Telemetry;

namespace Quiver.Storage.Wal;

/// <summary>
/// 単一ファイル WAL。旧来の <c>wal/</c> セグメント群 (<c>wal.NNNNNNNN.log</c>) を
/// 1 本のサイドカーファイル (例: <c>graph.quiver-wal</c>) に統合する。
/// <list type="bullet">
///   <item><see cref="Truncate"/> はセグメント削除の代わりにファイルをコンパクション
///     (truncate 対象 prefix を捨てて live tail を前詰め) する。checkpoint は
///     ActiveCount==0 のときに打たれるため live tail は小さい。</item>
///   <item><see cref="MarkDeleteOnDispose"/> されたクリーン終了では Dispose 時にファイルを削除する。
///     全データは graph.quiver へ durable 済みなので、静止時はサイドカーが消えて本体のみが残る。</item>
/// </list>
/// レコードフォーマット / 案C コアレス / group commit / PageImage coalesce は据え置き。
/// </summary>
internal sealed class WriteAheadLog : IWriteAheadLog
{
    // ヘッダレイアウト: Length(4) + Lsn(8) + TxId(8) + Type(1) + Crc32C(4) = 25 バイト
    internal const int HeaderSize = 25;
    private const int WriteBufferSize = 1024 * 1024;
    internal const int MaxPayloadSize = 8 * 1024 * 1024;

    private readonly string _path;
    private readonly object _writeLock = new();
    private readonly Channel<FlushRequest> _flushChannel;
    private readonly Task _flushTask;
    // group commit window を Stopwatch tick に変換して保持 (0 = 無効)。
    private readonly long _groupCommitWindowTicks;
    // 観測用カウンタ。
    private long _flushBatchCount;
    private long _flushRequestCount;

    // 複数 tx の PageImage を Commit/CheckpointBegin/CheckpointEnd の直前にまとめて
    // drain する共有 coalesce バッファ。`(fileKind, pageId)` ごとに「最後に書いた tx」の
    // payload を 1 件だけ保持し、同一ページに対する重複 PageImage 出力を抑制する。
    private readonly Dictionary<(byte FileKind, long PageId), CoalescedPageImage> _coalescedPageImages = new();
    private long _coalescedPageImageCount;
    private long _drainedPageImageCount;

    private long _nextLsn;
    private long _flushedLsn = -1;
    private long _bytesWritten;
    private FileStream? _stream;
    private readonly byte[] _buffer = new byte[WriteBufferSize];
    private int _bufPos;
    private bool _disposed;
    // クリーン終了時に WAL ファイルを削除するフラグ。backend が最終 flush 後に立てる。
    private bool _deleteOnDispose;

    public long CurrentLsn => Volatile.Read(ref _nextLsn) - 1;
    public long FlushedLsn => Volatile.Read(ref _flushedLsn);
    public long BytesWritten => Volatile.Read(ref _bytesWritten);

    /// <summary>バックグラウンドフラッシュループが実際に fsync を起動した回数。</summary>
    public long FlushBatchCount => Volatile.Read(ref _flushBatchCount);

    /// <summary><see cref="FlushTo"/> 経由でフラッシュ要求された累計回数。</summary>
    public long FlushRequestCount => Volatile.Read(ref _flushRequestCount);

    /// <summary>cross-tx de-dup ヒット数。</summary>
    public long CoalescedPageImageCount => Volatile.Read(ref _coalescedPageImageCount);

    /// <summary>coalesce バッファから drain された PageImage 件数の累計。</summary>
    public long DrainedPageImageCount => Volatile.Read(ref _drainedPageImageCount);

    public WriteAheadLog(string path)
        : this(path, 0, TimeSpan.Zero) { }

    public WriteAheadLog(string path, long segmentCapacity)
        : this(path, segmentCapacity, TimeSpan.Zero) { }

    public WriteAheadLog(string path, long segmentCapacity, TimeSpan groupCommitWindow)
    {
        _path = path;
        _ = segmentCapacity; // 単一ファイルでは未使用 (旧 API 互換のため受け取る)
        _groupCommitWindowTicks = groupCommitWindow > TimeSpan.Zero
            ? (long)(groupCommitWindow.TotalSeconds * Stopwatch.Frequency)
            : 0;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _flushChannel = Channel.CreateUnbounded<FlushRequest>(
            new UnboundedChannelOptions { SingleReader = true });
        RebuildState();
        _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
        _flushTask = Task.Factory.StartNew(
            RunFlushLoopAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// クリーン終了の最終 flush 後に backend が呼ぶ。全データが graph.quiver へ
    /// durable 化された後なので、Dispose で WAL ファイルを削除して静止時を単一ファイルにする。
    /// </summary>
    public void MarkDeleteOnDispose() => _deleteOnDispose = true;

    public long Append(WalRecordType type, TransactionId tx, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new StorageException($"WAL payload size {payload.Length} exceeds max {MaxPayloadSize}");

        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Commit / Checkpoint sentinel / Abort の直前で coalesce バッファを drain。
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

        long lsn = _nextLsn++;
        if (recordSize > WriteBufferSize)
        {
            // バッファより大きいレコードは直接ストリームへ書く (PageImage は通常 ~8KB なので稀)。
            FlushBufferLocked();
            WriteRecordDirectLocked(lsn, type, txIdValue, payload);
        }
        else
        {
            if (_bufPos + recordSize > WriteBufferSize)
                FlushBufferLocked();
            WriteRecordToBuffer(lsn, type, txIdValue, payload);
        }
        _bytesWritten += recordSize;
        QuiverTelemetry.WalBytesWritten.Add(recordSize);
        QuiverEventSource.Log.WalBytesWritten(recordSize);
        return lsn;
    }

    /// <summary>
    /// PageImage を共有 coalesce バッファへ投入する (詳細は旧実装と同一)。
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
                    // 別 tx が同一ページに書こうとした: 既存エントリを drain して per-tx 帰属を保持。
                    WriteRecordLocked(WalRecordType.PageImage, existing.Tx.Value, existing.Payload);
                    Interlocked.Increment(ref _drainedPageImageCount);
                }
                else
                {
                    Interlocked.Increment(ref _coalescedPageImageCount);
                }
            }
            _coalescedPageImages[key] = new CoalescedPageImage(tx, payload);
        }
    }

    /// <summary><paramref name="tx"/> が coalesce バッファに残しているエントリをすべて除去する。</summary>
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
        using var activity = QuiverTelemetry.WalFlushActivitySource.StartActivity(
            "wal.flush", ActivityKind.Internal);
        activity?.SetTag("quiver.wal.target_lsn", lsn);
        var sw = Stopwatch.StartNew();
        QuiverEventSource.Log.WalFlushRequestStarted();
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_flushChannel.Writer.TryWrite(new FlushRequest(lsn, tcs)))
            {
                if (Volatile.Read(ref _flushedLsn) >= lsn) return;
                throw new ObjectDisposedException(nameof(WriteAheadLog));
            }
            tcs.Task.GetAwaiter().GetResult();
            QuiverTelemetry.WalFlushDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            QuiverEventSource.Log.WalFlushed(lsn, sw.Elapsed.TotalMilliseconds);
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
        Span<byte> payload = stackalloc byte[9];
        payload[0] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload[1..], newPageCount);
        long lsn = Append(WalRecordType.FileTruncate, new TransactionId(-1), payload);
        FlushTo(lsn);
        return lsn;
    }

    /// <summary>
    /// 単一ファイルをコンパクションする。<paramref name="uptoLsn"/> 以下の LSN を持つ
    /// 先頭レコード群を捨て、LSN &gt; uptoLsn の live tail を前詰めする。すべて捨てられる場合は
    /// ファイルを空にする。crash 安全のため tail を temp ファイルへ書いてから atomic rename する。
    /// </summary>
    public void Truncate(long uptoLsn)
    {
        lock (_writeLock)
        {
            if (_disposed || _stream == null) return;
            FlushBufferLocked();
            _stream.Flush();

            long keepFromOffset = FindOffsetOfFirstLsnGreaterThan(uptoLsn);
            if (keepFromOffset == 0) return; // 何も捨てない (先頭から live)

            if (keepFromOffset < 0)
            {
                // 全レコードが uptoLsn 以下 → ファイルを空にする。
                _stream.SetLength(0);
                _stream.Seek(0, SeekOrigin.Begin);
                return;
            }

            CompactTailLocked(keepFromOffset);
        }
    }

    public IWalReader OpenReader(long startLsn) => new WalReader(_path, startLsn);

    public void Dispose()
    {
        if (_disposed) return;

        if (_deleteOnDispose)
        {
            // クリーン終了: データは graph.quiver へ durable 済み。WAL は捨てるので最終フラッシュ不要。
            _disposed = true;
            _flushChannel.Writer.TryComplete();
            try { _flushTask.GetAwaiter().GetResult(); } catch { }
            lock (_writeLock)
            {
                _stream?.Dispose();
                _stream = null;
            }
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            return;
        }

        // Dispose 完了前に coalesce バッファを最終 drain する。
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
            _stream?.Flush(flushToDisk: true);
            _stream?.Dispose();
            _stream = null;
        }
    }

    // -----------------------------------------------------------------------
    // 書き込みヘルパ (すべて _writeLock の下で呼ばれる)
    // -----------------------------------------------------------------------

    private void WriteRecordToBuffer(long lsn, WalRecordType type, long txId, ReadOnlySpan<byte> payload)
    {
        int recordSize = HeaderSize + payload.Length;
        Span<byte> dest = _buffer.AsSpan(_bufPos, recordSize);
        EncodeRecord(dest, lsn, type, txId, payload);
        _bufPos += recordSize;
    }

    private void WriteRecordDirectLocked(long lsn, WalRecordType type, long txId, ReadOnlySpan<byte> payload)
    {
        if (_stream == null) return;
        int recordSize = HeaderSize + payload.Length;
        byte[] tmp = System.Buffers.ArrayPool<byte>.Shared.Rent(recordSize);
        try
        {
            EncodeRecord(tmp.AsSpan(0, recordSize), lsn, type, txId, payload);
            _stream.Write(tmp, 0, recordSize);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(tmp); }
    }

    private static void EncodeRecord(Span<byte> dest, long lsn, WalRecordType type, long txId, ReadOnlySpan<byte> payload)
    {
        int recordSize = HeaderSize + payload.Length;
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
    }

    private void FlushBufferLocked()
    {
        if (_bufPos == 0 || _stream == null) return;
        _stream.Write(_buffer, 0, _bufPos);
        _bufPos = 0;
    }

    // -----------------------------------------------------------------------
    // コンパクション
    // -----------------------------------------------------------------------

    // LSN > uptoLsn の最初のレコードのファイル先頭からのオフセットを返す。
    // 全レコードが uptoLsn 以下なら -1、先頭から live なら 0。
    private long FindOffsetOfFirstLsnGreaterThan(long uptoLsn)
    {
        using var rs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        while (true)
        {
            long recStart = rs.Position;
            if (!WalReader.TryReadRecord(rs, out var rec)) return -1;
            if (rec.Type == WalRecordType.EndOfSegment) continue; // 旧形式互換 (新規には現れない)
            if (rec.Lsn > uptoLsn) return recStart;
        }
    }

    private void CompactTailLocked(long keepFromOffset)
    {
        if (_stream == null) return;
        long len = _stream.Length;
        int tailLen = checked((int)(len - keepFromOffset));
        byte[] tail = new byte[tailLen];
        _stream.Seek(keepFromOffset, SeekOrigin.Begin);
        _stream.ReadExactly(tail, 0, tailLen);

        // crash 安全: temp ファイルへ live tail を書いて fsync → atomic rename。
        string tmp = _path + ".compact";
        using (var ts = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            ts.Write(tail, 0, tailLen);
            ts.Flush(flushToDisk: true);
        }
        _stream.Dispose();
        try
        {
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException)
        {
            // 並行ハンドル (snapshot のファイルコピー等) が _path を開いていて rename できない場合は
            // 今回のコンパクションを諦める。File.Move は atomic なので _path は元の全内容のまま。
            // WAL は縮まないが correctness は保たれ、次の checkpoint で再試行される。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _stream.Seek(0, SeekOrigin.End);
            return;
        }
        _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
    }

    // -----------------------------------------------------------------------
    // フラッシュループ (バックグラウンドタスク — グループコミット)
    // -----------------------------------------------------------------------

    private async Task RunFlushLoopAsync()
    {
        var pending = new List<FlushRequest>();
        while (await _flushChannel.Reader.WaitToReadAsync())
        {
            // group commit window。
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
                _stream?.Flush(flushToDisk: true);
                highestLsn = _nextLsn > 0 ? _nextLsn - 1 : -1;
                Volatile.Write(ref _flushedLsn, highestLsn);
            }
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
    // 状態復元
    // -----------------------------------------------------------------------

    private void RebuildState()
    {
        _nextLsn = 0;
        if (!File.Exists(_path)) return;

        long maxLsn = -1;
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            while (WalReader.TryReadRecord(fs, out WalRecord rec))
            {
                if (rec.Type == WalRecordType.EndOfSegment) continue;
                if (rec.Lsn > maxLsn) maxLsn = rec.Lsn;
            }
        }
        catch { }

        _nextLsn = maxLsn >= 0 ? maxLsn + 1 : 0;
    }

    private readonly record struct FlushRequest(long TargetLsn, TaskCompletionSource<bool> Tcs);
}

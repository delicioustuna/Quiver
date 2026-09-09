using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using Yatagarasu.Core;
using Yatagarasu.Telemetry;
using Yatagarasu.Storage.Wal;

namespace Yatagarasu.Storage;

/// <summary>
/// MemoryMappedFile + Clock バッファプールによる IPagedFile 実装。
/// ページ 0 をメタデータページとして使用し、Free List と総ページ数を管理する。
/// </summary>
internal sealed class PagedFile : IPagedFile
{
    public const int PageSizeConst = 8192;
    public const int BodySize = PageSizeConst - PageHeader.Size;
    private const int DefaultPoolCapacity = 256;
    internal const long DefaultInitialFileAllocationBytes = 1L * 1024 * 1024;
    internal const long DefaultMaximumFileGrowthStepBytes = 64L * 1024 * 1024;

    // メタページ (page 0) の body 内オフセット
    private const int MetaOffsetFirstFree = 0;  // int64: Free List 先頭 PageId (-1 = 空)
    private const int MetaOffsetPageCount = 8;  // int64: 論理割り当て済みページ数
    private static readonly PageId MetaPageId = new(0);

    private readonly string _path;
    private readonly int _poolCapacity;
    private readonly long _initialFileAllocationBytes;
    private readonly long _maximumFileGrowthStepBytes;
    private readonly Lock _poolLock = new();
    private readonly PoolShard[] _shards = Enumerable.Range(0, 16).Select(_ => new PoolShard()).ToArray();

    private FileStream _fileStream;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _viewAccessor;
    // 論理的に割り当て済みのページ数 (メタページの PageCount と同期)
    private long _logicalPageCount;

    private readonly PoolFrame[] _frames;
    private int _clockHand;
    private bool _disposed;
    private byte? _walFileKind;
    // WAL を先行フラッシュ (write-ahead) するために保持する。EnableWalLogging で配線。
    private IWriteAheadLog? _wal;
    // dotnet-counters の buffer-pool-size-bytes gauge へ提供する provider 登録ハンドル。
    private readonly IDisposable _bufferPoolSizeRegistration;

    int IPagedFile.PageSize => PageSizeConst;
    public long PageCount => Volatile.Read(ref _logicalPageCount);
    public string Path => _path;

    public PagedFile(
        string path,
        int poolCapacity = DefaultPoolCapacity,
        long initialFileAllocationBytes = DefaultInitialFileAllocationBytes,
        long maximumFileGrowthStepBytes = DefaultMaximumFileGrowthStepBytes)
    {
        if (poolCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(poolCapacity));

        _path = path;
        _poolCapacity = poolCapacity;
        _initialFileAllocationBytes = NormalizeAllocationOption(
            initialFileAllocationBytes, nameof(initialFileAllocationBytes));
        _maximumFileGrowthStepBytes = NormalizeAllocationOption(
            maximumFileGrowthStepBytes, nameof(maximumFileGrowthStepBytes));
        _frames = new PoolFrame[poolCapacity];
        for (int i = 0; i < poolCapacity; i++)
            _frames[i] = new PoolFrame();

        // 本 PagedFile が保有するバッファプールサイズを EventCounters の gauge に登録する。
        // 複数 PagedFile (data + index 等) が並存しても合算されて 1 つのメトリクスとして出る。
        long bufferPoolBytes = (long)_poolCapacity * PageSizeConst;
        _bufferPoolSizeRegistration =
            YatagarasuEventSource.Log.RegisterBufferPoolSizeBytesProvider(() => bufferPoolBytes);

        bool isNew = !File.Exists(path);
        _fileStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        if (isNew)
        {
            EnsureRawFileSize(1);
            MapFile();
            _logicalPageCount = 1;
            InitMetaPage();
        }
        else
        {
            MapFile();
            _logicalPageCount = ReadLogicalPageCountFromMeta();
        }
    }

    public PageId AllocatePage(PageKind kind)
    {
        using (EnterPoolCoordination())
        {
            if (_wal?.ActiveWriteSet is not null)
                return AllocatePageNoStealLocked(kind);

            // 直前の no-force commit が meta/free-list の最新状態を frame に残している場合がある。
            // transaction 外の allocation は、その committed dirty state を MMF へ反映してから読む。
            FlushDirtyFramesLocked();
            byte[] metaBuf = ArrayPool<byte>.Shared.Rent(PageSizeConst);
            try
            {
                MmfReadPage(MetaPageId, metaBuf);
                Span<byte> metaBody = metaBuf.AsSpan(PageHeader.Size);

                long firstFree = BinaryPrimitives.ReadInt64LittleEndian(metaBody[MetaOffsetFirstFree..]);
                long pageCount = BinaryPrimitives.ReadInt64LittleEndian(metaBody[MetaOffsetPageCount..]);

                PageId newPageId;

                if (firstFree >= 0)
                {
                    // Free List から再利用
                    newPageId = new PageId(firstFree);

                    byte[] freeBuf = ArrayPool<byte>.Shared.Rent(PageSizeConst);
                    try
                    {
                        MmfReadPage(newPageId, freeBuf);
                        // freed page body[0..7] に次の Free ページ ID が入っている
                        long nextFree = BinaryPrimitives.ReadInt64LittleEndian(freeBuf.AsSpan(PageHeader.Size));
                        BinaryPrimitives.WriteInt64LittleEndian(metaBody[MetaOffsetFirstFree..], nextFree);

                        freeBuf.AsSpan(0, PageSizeConst).Clear();
                        PageHeader.Write(freeBuf.AsSpan(0, PageSizeConst), newPageId, kind, lsn: 0);
                        MmfWritePageAndSync(newPageId, freeBuf);
                    }
                    finally { ArrayPool<byte>.Shared.Return(freeBuf); }
                }
                else
                {
                    // ファイル末尾に新規ページを割り当て
                    newPageId = new PageId(pageCount);
                    EnsureFileSizeAndRemapLocked(pageCount + 1);

                    byte[] newBuf = ArrayPool<byte>.Shared.Rent(PageSizeConst);
                    try
                    {
                        Array.Clear(newBuf, 0, PageSizeConst);
                        PageHeader.Write(newBuf.AsSpan(0, PageSizeConst), newPageId, kind, lsn: 0);
                        MmfWritePageAndSync(newPageId, newBuf);
                    }
                    finally { ArrayPool<byte>.Shared.Return(newBuf); }

                    BinaryPrimitives.WriteInt64LittleEndian(metaBody[MetaOffsetPageCount..], pageCount + 1);
                    _logicalPageCount = pageCount + 1;
                }

                // メタページをヘッダ再計算して書き戻す
                PageHeader.Write(metaBuf.AsSpan(0, PageSizeConst), MetaPageId, PageKind.Header, lsn: 0);
                MmfWritePageAndSync(MetaPageId, metaBuf);

                return newPageId;
            }
            finally { ArrayPool<byte>.Shared.Return(metaBuf); }
        }
    }

    /// <summary>
    /// active writer 中の allocation metadata を通常の dirty frame として更新する。
    /// 末尾ページの物理領域は到達不能なため先に確保できるが、meta page と free-list page は
    /// strict Commit 前にデータファイルへ公開しない。
    /// </summary>
    private PageId AllocatePageNoStealLocked(PageKind kind)
    {
        var meta = PinForWrite(MetaPageId);
        try
        {
            Span<byte> metaBody = meta.Data;
            long firstFree = BinaryPrimitives.ReadInt64LittleEndian(
                metaBody[MetaOffsetFirstFree..]);
            long pageCount = BinaryPrimitives.ReadInt64LittleEndian(
                metaBody[MetaOffsetPageCount..]);

            if (firstFree >= 0)
            {
                var newPageId = new PageId(firstFree);
                var free = PinForWrite(newPageId);
                try
                {
                    long nextFree = BinaryPrimitives.ReadInt64LittleEndian(free.Data);
                    BinaryPrimitives.WriteInt64LittleEndian(
                        metaBody[MetaOffsetFirstFree..],
                        nextFree);
                    free.Raw.Clear();
                    PageHeader.Write(free.Raw, newPageId, kind, lsn: 0);
                }
                finally
                {
                    free.Dispose();
                }
                return newPageId;
            }

            var appendedPageId = new PageId(pageCount);
            EnsureFileSizeAndRemapLocked(pageCount + 1);

            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(PageSizeConst);
            try
            {
                newBuffer.AsSpan(0, PageSizeConst).Clear();
                PageHeader.Write(
                    newBuffer.AsSpan(0, PageSizeConst),
                    appendedPageId,
                    kind,
                    lsn: 0);
                // 既存の committed 構造から到達不能な末尾領域だけを物理確保する。
                MmfWritePageAndSync(appendedPageId, newBuffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(newBuffer);
            }

            BinaryPrimitives.WriteInt64LittleEndian(
                metaBody[MetaOffsetPageCount..],
                pageCount + 1);
            _logicalPageCount = pageCount + 1;
            return appendedPageId;
        }
        finally
        {
            meta.Dispose();
        }
    }

    public void FreePage(PageId pageId)
    {
        using (EnterPoolCoordination())
        {
            if (_wal?.ActiveWriteSet is not null)
            {
                FreePageNoStealLocked(pageId);
                return;
            }

            FlushDirtyFramesLocked();
            byte[] metaBuf = ArrayPool<byte>.Shared.Rent(PageSizeConst);
            byte[] freeBuf = ArrayPool<byte>.Shared.Rent(PageSizeConst);
            try
            {
                MmfReadPage(MetaPageId, metaBuf);
                Span<byte> metaBody = metaBuf.AsSpan(PageHeader.Size);

                long currentFirstFree = BinaryPrimitives.ReadInt64LittleEndian(metaBody[MetaOffsetFirstFree..]);

                // freed page: body[0..7] = 現在の Free List 先頭
                freeBuf.AsSpan(0, PageSizeConst).Clear();
                BinaryPrimitives.WriteInt64LittleEndian(freeBuf.AsSpan(PageHeader.Size), currentFirstFree);
                PageHeader.Write(freeBuf.AsSpan(0, PageSizeConst), pageId, PageKind.Free, lsn: 0);
                MmfWritePageAndSync(pageId, freeBuf);

                // メタページ更新: firstFree = pageId
                BinaryPrimitives.WriteInt64LittleEndian(metaBody[MetaOffsetFirstFree..], pageId.Value);
                PageHeader.Write(metaBuf.AsSpan(0, PageSizeConst), MetaPageId, PageKind.Header, lsn: 0);
                MmfWritePageAndSync(MetaPageId, metaBuf);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(metaBuf);
                ArrayPool<byte>.Shared.Return(freeBuf);
            }
        }
    }

    private int _readOnlyAnalysisCount;

    /// <summary>書き込み権限を保持した解析中に、未常駐ページの読み取りが変更済みページを書き出すのを防ぐ。</summary>
    internal IDisposable BeginReadOnlyAnalysis()
    {
        Interlocked.Increment(ref _readOnlyAnalysisCount);
        return new ReadOnlyAnalysisScope(this);
    }

    private sealed class ReadOnlyAnalysisScope(PagedFile file) : IDisposable
    {
        private PagedFile? _file = file;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _file, null);
            if (current is not null) Interlocked.Decrement(ref current._readOnlyAnalysisCount);
        }
    }

    public PageReadHandle PinForRead(PageId pageId)
    {
        int frame;
        if (Volatile.Read(ref _readOnlyAnalysisCount) != 0)
        {
            using (EnterPoolCoordination())
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!GetShard(pageId).Pages.ContainsKey(pageId))
                {
                    if (pageId.Value < 0 || pageId.Value >= PageCount)
                        throw new ArgumentOutOfRangeException(nameof(pageId));
                    // 常駐する変更済みフレームを最新値の正本とする。未常駐ページだけを別のバッファへ読み、退避のための書き出しを避ける。
                    byte[] snapshot = new byte[PageSizeConst];
                    MmfReadPage(pageId, snapshot);
                    PageHeader.Validate(snapshot, pageId);
                    return new PageReadHandle(pageId, snapshot);
                }
                frame = GetOrLoadFrame(pageId);
            }
        }
        else frame = GetOrLoadFrame(pageId);
        // ハンドル生存中、他スレッドからの書き込みからバッファを保護する。
        // 検証はロード時に済んでいるので pin ごとの再検証はしない (上記 GetOrLoadFrame 参照)。
        bool readLocked = false;
        try
        {
            _frames[frame].FrameLock.EnterReadLock();
            readLocked = true;
            return new PageReadHandle(this, new ReadPageLease(pageId, frame, _frames[frame].Generation), ReadFrameSpan(frame));
        }
        catch
        {
            if (readLocked) _frames[frame].FrameLock.ExitReadLock();
            Interlocked.Decrement(ref _frames[frame].PinCount);
            throw;
        }
    }

    public PageWriteHandle PinForWrite(PageId pageId)
    {
        int frame = GetOrLoadFrame(pageId);
        // ハンドル生存中、他スレッドからの読み書きを排他する。
        bool writeLocked = false;
        try
        {
            _frames[frame].FrameLock.EnterWriteLock();
            writeLocked = true;
            Span<byte> raw = ReadFrameSpan(frame);
            // 検証はロード時に済んでいるので pin ごとの再検証はしない (GetOrLoadFrame 参照)。
            // この書き込みトランザクション内で本ページを初めて pin する時点の内容を
            // before-image として捕捉する。caller がまだ変更していないこの瞬間が唯一の機会。
            // frame は pin 済みなので evict されず、span は安定している。
            // before-image は in-process abort/savepoint 用に transaction-owned write set へ捕捉する。
            if (_walFileKind is byte fileKind)
            {
                _wal?.ActiveWriteSet?.CaptureBeforeImage(fileKind, pageId.Value, raw);
            }
            return new PageWriteHandle(this, new WritePageLease(pageId, frame, _frames[frame].Generation), raw);
        }
        catch
        {
            if (writeLocked) _frames[frame].FrameLock.ExitWriteLock();
            Interlocked.Decrement(ref _frames[frame].PinCount);
            throw;
        }
    }

    void IPagedFile.ReleaseRead(ReadPageLease lease)
    {
        if ((uint)lease.FrameIndex >= (uint)_frames.Length)
            throw new InvalidOperationException("Invalid read page frame.");
        PoolFrame frame = _frames[lease.FrameIndex];
        if (frame.PageId != lease.PageId || frame.Generation != lease.Generation || !frame.FrameLock.IsReadLockHeld)
            throw new InvalidOperationException("Read page lease is stale or is not held by this thread.");
        lease.ClaimRelease();
        // 固定数が正の間は再割り当てされない。ロックを解放してから最後に固定を解除する。
        frame.FrameLock.ExitReadLock();
        Interlocked.Decrement(ref frame.PinCount);
    }

    private PoolFrame ClaimWriteRelease(WritePageLease lease)
    {
        if ((uint)lease.FrameIndex >= (uint)_frames.Length)
            throw new InvalidOperationException("Invalid write page frame.");
        PoolFrame frame = _frames[lease.FrameIndex];
        if (frame.PageId != lease.PageId || frame.Generation != lease.Generation || !frame.FrameLock.IsWriteLockHeld)
            throw new InvalidOperationException("Write page lease is stale or is not held by this thread.");
        lease.ClaimRelease();
        return frame;
    }

    void IPagedFile.ReleaseWriteUnchanged(WritePageLease lease)
    {
        PoolFrame frame = ClaimWriteRelease(lease);
        frame.FrameLock.ExitWriteLock();
        Interlocked.Decrement(ref frame.PinCount);
    }

    void IPagedFile.ReleaseWriteDirty(WritePageLease lease, long lsn)
    {
        PoolFrame frame = ClaimWriteRelease(lease);
        WalWriteSet? writeSet = _wal?.ActiveWriteSet;
        // 書き出しはフレームの読み取りロック、追い出しは固定数で排他する。変更所有者を公開してから固定を解除する。
        frame.IsDirty = true;
        frame.DirtyTransactionId = writeSet?.TransactionId.Value ?? 0;
        try
        {
            // WAL ログ書き込み前にヘッダの LSN とチェックサムを更新し、ページイメージを有効化する。
            PageHeader.UpdateLsnAndChecksum(frame.Buffer.AsSpan(), lsn);

            // WAL-first: クラッシュリカバリでコミット済み書き込みを再生できるよう、ページイメージをログに残す。
            if (_walFileKind is byte fileKind)
                writeSet?.LogPageImage(
                    fileKind,
                    lease.PageId.Value,
                    frame.Buffer.AsSpan(0, PageSizeConst),
                    committedLsn => StampCommittedLsn(
                        lease.PageId,
                        writeSet.TransactionId,
                        committedLsn));
        }
        finally
        {
            frame.FrameLock.ExitWriteLock();
            Interlocked.Decrement(ref frame.PinCount);
        }
    }

    public void EnableWalLogging(byte fileKind, IWriteAheadLog wal)
    {
        _walFileKind = fileKind;
        _wal = wal;
    }

    public void WritePageForRecovery(PageId pageId, ReadOnlySpan<byte> pageBytes)
    {
        if (pageBytes.Length != PageSizeConst) return;
        PageHeader.Validate(pageBytes, pageId);
        using (EnterPoolCoordination())
        {
            EnsureFileSizeAndRemapLocked(pageId.Value + 1);

            long offset = pageId.Value * (long)PageSizeConst;
            byte[] buf = ArrayPool<byte>.Shared.Rent(PageSizeConst);
            try
            {
                pageBytes.CopyTo(buf);
                _viewAccessor!.WriteArray(offset, buf, 0, PageSizeConst);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }

            // メタページが replay された場合はインメモリのページ数を再計算する。
            if (pageId == MetaPageId)
                _logicalPageCount = ReadLogicalPageCountFromMeta();
            else
                _logicalPageCount = Math.Max(_logicalPageCount, pageId.Value + 1);

            // 後続の読み出しが復旧済み内容を参照できるよう、キャッシュ済みフレームを無効化する。
            if (GetShard(pageId).Pages.TryGetValue(pageId, out int frame))
            {
                pageBytes.CopyTo(_frames[frame].Buffer.AsSpan());
                _frames[frame].IsDirty = false;
                _frames[frame].DirtyTransactionId = 0;
            }
        }
    }

    private void FreePageNoStealLocked(PageId pageId)
    {
        var meta = PinForWrite(MetaPageId);
        try
        {
            long currentFirstFree = BinaryPrimitives.ReadInt64LittleEndian(
                meta.Data[MetaOffsetFirstFree..]);
            var freed = PinForWrite(pageId);
            try
            {
                freed.Raw.Clear();
                BinaryPrimitives.WriteInt64LittleEndian(
                    freed.Data,
                    currentFirstFree);
                PageHeader.Write(freed.Raw, pageId, PageKind.Free, lsn: 0);
            }
            finally
            {
                freed.Dispose();
            }

            BinaryPrimitives.WriteInt64LittleEndian(
                meta.Data[MetaOffsetFirstFree..],
                pageId.Value);
        }
        finally
        {
            meta.Dispose();
        }
    }

    public long ReadPageLsnForRecovery(PageId pageId)
    {
        using (EnterPoolCoordination())
        {
            if (GetShard(pageId).Pages.TryGetValue(pageId, out int frame))
            {
                try
                {
                    PageHeader.Validate(_frames[frame].Buffer, pageId);
                    return PageHeader.ReadLsn(_frames[frame].Buffer);
                }
                catch (YatagarasuException)
                {
                    return -1;
                }
            }

            long offset = pageId.Value * (long)PageSizeConst;
            if (pageId.Value < 0 || offset + PageSizeConst > _fileStream.Length)
                return -1;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(PageSizeConst);
            try
            {
                MmfReadPage(pageId, buffer);
                PageHeader.Validate(buffer.AsSpan(0, PageSizeConst), pageId);
                return PageHeader.ReadLsn(buffer);
            }
            catch (YatagarasuException)
            {
                return -1;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public void Flush()
    {
        using (EnterPoolCoordination())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FlushDirtyFramesLocked();
            _viewAccessor?.Flush();
            _fileStream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// ページファイルを <paramref name="newPageCount"/> へ物理 truncate する。
    /// 現在のページ数より大きい値を渡すと no-op (拡張は行わない)。バッファプール上で
    /// PageId >= newPageCount のフレームを drop、MMF を unmap、<c>SetLength</c>、remap、
    /// メタページの PageCount を書き戻して fsync する。
    /// </summary>
    /// <remarks>
    /// 呼び出し側 (<c>Vacuum</c> / <c>RecoveryManager</c>) は事前に
    /// <see cref="IWriteAheadLog.WriteFileTruncate"/> を書いて durable 化する。
    /// 再 mmf 範囲は <c>PageSizeConst</c> を下限とする (空ファイルは作らない)。
    /// </remarks>
    public void Truncate(long newPageCount)
    {
        if (newPageCount < 1)
            throw new ArgumentOutOfRangeException(
                nameof(newPageCount), "newPageCount must be >= 1 (meta page must be retained).");
        using (EnterPoolCoordination())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_wal?.ActiveWriteSet is not null)
                throw new TransactionException(
                    "Cannot truncate a page file while a write transaction is active.");
            if (newPageCount >= _logicalPageCount) return;

            // 1. 削除対象範囲のキャッシュフレームを drop (dirty も discard — vacuum 前提で
            //    呼ばれるので、未コミット変更は存在しない / 既に flush 済み)。
            for (int i = 0; i < _poolCapacity; i++)
            {
                ref PoolFrame f = ref _frames[i];
                if (!f.PageId.IsValid) continue;
                if (f.PageId.Value < newPageCount) continue;
                if (f.PinCount > 0)
                    throw new InvalidOperationException(
                        $"Cannot truncate: page {f.PageId.Value} is pinned (PinCount={f.PinCount}).");
                GetShard(f.PageId).Pages.Remove(f.PageId);
                f.PageId = PageId.Invalid;
                f.IsDirty = false;
                f.DirtyTransactionId = 0;
                f.Referenced = false;
            }

            // 2. 残るフレーム (truncate 範囲外) のダーティをファイルへ反映。
            FlushDirtyFramesLocked();
            _viewAccessor?.Flush();

            // 3. MMF を一旦 unmap してから SetLength → 再 map。
            long newLength = newPageCount * (long)PageSizeConst;
            _viewAccessor?.Dispose();
            _mmf?.Dispose();
            _mmf = null;
            _viewAccessor = null;
            _fileStream.SetLength(newLength);
            _fileStream.Flush(flushToDisk: true);
            MapFile();

            // 4. メタページの PageCount を更新 (firstFree はそのまま — vacuum 前提では空)。
            _logicalPageCount = newPageCount;
            byte[] metaBuf = ArrayPool<byte>.Shared.Rent(PageSizeConst);
            try
            {
                MmfReadPage(MetaPageId, metaBuf);
                BinaryPrimitives.WriteInt64LittleEndian(
                    metaBuf.AsSpan(PageHeader.Size + MetaOffsetPageCount), newPageCount);
                PageHeader.Write(metaBuf.AsSpan(0, PageSizeConst), MetaPageId, PageKind.Header, lsn: 0);
                MmfWritePageAndSync(MetaPageId, metaBuf);
            }
            finally { ArrayPool<byte>.Shared.Return(metaBuf); }

            _viewAccessor?.Flush();
            _fileStream.Flush(flushToDisk: true);
        }
    }

    // ------------------------------------------------------------------
    // バッファプール内部実装
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetOrLoadFrame(PageId pageId)
    {
        PoolShard shard = GetShard(pageId);
        int frame;
        lock (shard.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (shard.Pages.TryGetValue(pageId, out frame))
            {
                _frames[frame].Referenced = true;
                Interlocked.Increment(ref _frames[frame].PinCount);
            }
            else frame = -1;
        }
        if (frame < 0) return LoadFrame(pageId);
        try
        {
            // MeterListenerのコールバックが全体操作へ再入しても、シャードから全体ロックへの逆順取得を避ける。
            YatagarasuTelemetry.BufferPoolHits.Add(1);
            YatagarasuEventSource.Log.BufferPoolHit();
            return frame;
        }
        catch
        {
            Interlocked.Decrement(ref _frames[frame].PinCount);
            throw;
        }
    }

    private int LoadFrame(PageId pageId)
    {
        using (EnterPoolCoordination())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (GetShard(pageId).Pages.TryGetValue(pageId, out int existing))
            {
                _frames[existing].Referenced = true;
                Interlocked.Increment(ref _frames[existing].PinCount);
                // バッファプールヒット計上 (lock 内で counter add は軽量)。
                YatagarasuTelemetry.BufferPoolHits.Add(1);
                YatagarasuEventSource.Log.BufferPoolHit();
                return existing;
            }

            int victim = FindVictim();
            // 既存ページを追い出してから新規ロードする場合のみ eviction としてカウント。
            // 起動直後の空フレーム埋めはミスにはなるが eviction ではない。
            bool wasEviction = _frames[victim].PageId.IsValid;
            EvictFrame(victim);

            _frames[victim].PageId = pageId;
            _frames[victim].Generation++;
            MmfReadPage(pageId, _frames[victim].Buffer);
            // checksum / magic / pageId の検証は disk→frame ロード時 (= ここ) のみ行う。
            // 常駐フレームの pin ごとに全ページ CRC を再計算するのは冗長 (RAM 上の内容は disk 破損に
            // 晒されず、書込は ReleaseWriteDirty で チェックサムを更新し、FrameLock が読み取りと書き込みを排他する)。
            // 破損ページの早期検出はロード時で十分。
            PageHeader.Validate(ReadFrameSpan(victim), pageId);
            _frames[victim].Referenced = true;
            _frames[victim].IsDirty = false;
            GetShard(pageId).Pages[pageId] = victim;
            Interlocked.Increment(ref _frames[victim].PinCount);
            // バッファプールミス (eviction + page-in 発生)。
            YatagarasuTelemetry.BufferPoolMisses.Add(1);
            YatagarasuEventSource.Log.BufferPoolMiss();
            if (wasEviction) YatagarasuEventSource.Log.BufferPoolEviction();
            return victim;
        }
    }

    // Clock (Second-Chance) アルゴリズム。全体ロックと全シャードのロックの保持下で呼び出す。
    private int FindVictim()
    {
        int probes = 0;
        int maximumProbes = checked(_poolCapacity * 2);
        while (probes++ < maximumProbes)
        {
            ref PoolFrame f = ref _frames[_clockHand];
            if (Volatile.Read(ref f.PinCount) == 0)
            {
                if (!f.Referenced)
                {
                    if (!IsUncommittedDirty(f))
                    {
                        int victim = _clockHand;
                        _clockHand = (_clockHand + 1) % _poolCapacity;
                        return victim;
                    }
                }
                else
                {
                    f.Referenced = false;
                }
            }
            _clockHand = (_clockHand + 1) % _poolCapacity;
        }

        WalWriteSet? active = _wal?.ActiveWriteSet;
        if (active is not null)
        {
            active.MarkTooLarge(_poolCapacity);
            throw new TransactionTooLargeException(
                active.TransactionId,
                _poolCapacity);
        }

        throw new StorageException(
            $"Buffer pool has no evictable frame among {_poolCapacity} pages.");
    }

    // 全体ロックと全シャードのロックの保持下で呼び出す。
    private void EvictFrame(int frame)
    {
        ref PoolFrame f = ref _frames[frame];
        if (f.PageId.IsValid)
        {
            if (f.IsDirty)
            {
                // 未 commit owner の frame は FindVictim が候補から除外する。ここへ到達する
                // dirty frame は Commit が durable 済みか transaction 外で作られたものだけである。
                FlushWalBeforeDataWrite();
                MmfWritePage(f.PageId, f.Buffer);
            }
            GetShard(f.PageId).Pages.Remove(f.PageId);
            f.PageId = PageId.Invalid;
            f.IsDirty = false;
            f.DirtyTransactionId = 0;
        }
    }

    private bool IsUncommittedDirty(PoolFrame frame)
    {
        if (!frame.IsDirty || frame.DirtyTransactionId == 0)
            return false;
        WalWriteSet? active = _wal?.ActiveWriteSet;
        return active is not null
            && active.TransactionId.Value == frame.DirtyTransactionId;
    }

    private void StampCommittedLsn(
        PageId pageId,
        TransactionId transactionId,
        long lsn)
    {
        using (EnterPoolCoordination())
        {
            if (!GetShard(pageId).Pages.TryGetValue(pageId, out int frame))
                return;
            ref PoolFrame current = ref _frames[frame];
            if (!current.IsDirty
                || current.DirtyTransactionId != transactionId.Value)
            {
                return;
            }
            PageHeader.UpdateLsnAndChecksum(current.Buffer, lsn);
        }
    }

    // ダーティページをデータファイルへ書き出す前に WAL を末尾まで先行フラッシュする。
    // FlushTo は flushedLsn が既に追いついていれば no-op なので、同一フラッシュ波の中で
    // 余計な fsync は発生しない。
    private void FlushWalBeforeDataWrite()
    {
        if (_wal is { } wal)
            wal.FlushTo(wal.CurrentLsn);
    }

    private Span<byte> ReadFrameSpan(int frame) => new Span<byte>(_frames[frame].Buffer);

    // ------------------------------------------------------------------
    // MMF アクセスヘルパー (いずれも 全体ロックと全シャードのロックの保持下で呼び出す)
    // ------------------------------------------------------------------

    private void MmfReadPage(PageId pageId, byte[] buffer)
    {
        _viewAccessor!.ReadArray(pageId.Value * PageSizeConst, buffer, 0, PageSizeConst);
    }

    private void MmfWritePage(PageId pageId, byte[] buffer)
    {
        _viewAccessor!.WriteArray(pageId.Value * PageSizeConst, buffer, 0, PageSizeConst);
    }

    // MMF に書き込み、かつキャッシュ上のフレームも同期する。
    private void MmfWritePageAndSync(PageId pageId, byte[] buffer)
    {
        MmfWritePage(pageId, buffer);
        if (GetShard(pageId).Pages.TryGetValue(pageId, out int frame))
        {
            buffer.AsSpan(0, PageSizeConst).CopyTo(_frames[frame].Buffer);
            _frames[frame].IsDirty = false;
            _frames[frame].DirtyTransactionId = 0;
        }
    }

    // ダーティなフレームを全て MMF に書き出す。全体ロックと全シャードのロックの保持下で呼び出す。
    private void FlushDirtyFramesLocked()
    {
        bool walFlushed = false;
        for (int i = 0; i < _poolCapacity; i++)
        {
            PoolFrame f = _frames[i];
            // プールのロックを保持して書き込み完了を待つと、書き込み側による別ページの取得と循環待ちになる。
            // 読み取り中の固定は保存を妨げない。同じスレッドが書き込み中のページも保存対象から外す。
            if (f.FrameLock.IsWriteLockHeld || !f.FrameLock.TryEnterReadLock(0)) continue;
            try
            {
                if (!f.IsDirty || !f.PageId.IsValid || IsUncommittedDirty(f)) continue;
                if (!walFlushed)
                {
                    FlushWalBeforeDataWrite();
                    walFlushed = true;
                }
                MmfWritePage(f.PageId, f.Buffer);
                f.IsDirty = false;
                f.DirtyTransactionId = 0;
            }
            finally { f.FrameLock.ExitReadLock(); }
        }
    }

    // ------------------------------------------------------------------
    // ファイルサイズ管理
    // ------------------------------------------------------------------

    // MMF マップ前(コンストラクタ内)に使用。
    private void EnsureRawFileSize(long pageCount)
    {
        long required = checked(pageCount * PageSizeConst);
        if (_fileStream.Length < required)
            _fileStream.SetLength(ComputeGrowthTarget(_fileStream.Length, required));
    }

    // 全体ロックと全シャードのロックの保持下で呼び出す。ファイル拡張時に MMF を再マップする。
    private void EnsureFileSizeAndRemapLocked(long pageCount)
    {
        long required = checked(pageCount * PageSizeConst);
        if (_fileStream.Length < required)
        {
            FlushDirtyFramesLocked();
            long grown = ComputeGrowthTarget(_fileStream.Length, required);
            _viewAccessor?.Flush();
            _viewAccessor?.Dispose();
            _mmf?.Dispose();
            _mmf = null;
            _viewAccessor = null;
            _fileStream.SetLength(grown);
            MapFile();
        }
    }

    private long ComputeGrowthTarget(long currentLength, long requiredLength)
    {
        long increment = Math.Min(
            Math.Max(currentLength, _initialFileAllocationBytes),
            _maximumFileGrowthStepBytes);
        long adaptiveTarget = checked(currentLength + increment);
        return AlignToPage(Math.Max(requiredLength, adaptiveTarget));
    }

    private static long NormalizeAllocationOption(long value, string parameterName)
    {
        if (value < PageSizeConst)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Allocation size must be at least one {PageSizeConst}-byte page.");
        return AlignToPage(value);
    }

    private static long AlignToPage(long value)
    {
        long remainder = value % PageSizeConst;
        return remainder == 0 ? value : checked(value + PageSizeConst - remainder);
    }

    private void MapFile()
    {
        long capacity = Math.Max(_fileStream.Length, PageSizeConst);
        _mmf = MemoryMappedFile.CreateFromFile(
            _fileStream, null, capacity,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true);
        _viewAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
    }

    // ------------------------------------------------------------------
    // メタページ初期化・読み取り
    // ------------------------------------------------------------------

    private void InitMetaPage()
    {
        byte[] buf = new byte[PageSizeConst];
        Span<byte> body = buf.AsSpan(PageHeader.Size);
        BinaryPrimitives.WriteInt64LittleEndian(body[MetaOffsetFirstFree..], -1L);
        BinaryPrimitives.WriteInt64LittleEndian(body[MetaOffsetPageCount..], 1L);
        // body 書き込み後にヘッダ(チェックサム含む)を計算して書き込む
        PageHeader.Write(buf.AsSpan(), MetaPageId, PageKind.Header, lsn: 0);
        _viewAccessor!.WriteArray(0, buf, 0, PageSizeConst);
        _viewAccessor.Flush();
    }

    private long ReadLogicalPageCountFromMeta()
    {
        byte[] buf = new byte[PageSizeConst];
        _viewAccessor!.ReadArray(0, buf, 0, PageSizeConst);
        PageHeader.Validate(buf.AsSpan(), MetaPageId);
        return BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(PageHeader.Size + MetaOffsetPageCount));
    }

    // ------------------------------------------------------------------

    public void Dispose()
    {
        using var coordination = EnterPoolCoordination();
        if (_disposed) return;
        Flush();
        _disposed = true;
        _viewAccessor?.Dispose();
        _mmf?.Dispose();
        _fileStream.Dispose();
        // dotnet-counters の gauge プロバイダから抜ける。
        _bufferPoolSizeRegistration.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PoolShard GetShard(PageId pageId) =>
        _shards[(int)((ulong)pageId.Value ^ ((ulong)pageId.Value >> 4)) & 15];

    // 常駐ページの取得は単一シャードだけをロックする。未常駐なら一度解放し、全体ロック、全シャードの昇順で取得して再検査する。
    // 再入可能な割り当てと書き出しも同じ順序を守り、フレームの再割り当てとMMFの寿命をまとめて保護する。
    private PoolCoordination EnterPoolCoordination() => new(_poolLock, _shards);

    private readonly struct PoolCoordination : IDisposable
    {
        private readonly Lock _global;
        private readonly PoolShard[] _shards;

        internal PoolCoordination(Lock global, PoolShard[] shards)
        {
            _global = global;
            _shards = shards;
            global.Enter();
            int acquired = 0;
            try
            {
                for (; acquired < shards.Length; acquired++) shards[acquired].Gate.Enter();
            }
            catch
            {
                while (acquired > 0) shards[--acquired].Gate.Exit();
                global.Exit();
                throw;
            }
        }

        public void Dispose()
        {
            for (int i = _shards.Length - 1; i >= 0; i--) _shards[i].Gate.Exit();
            _global.Exit();
        }
    }

    private sealed class PoolShard
    {
        internal readonly Lock Gate = new();
        internal readonly Dictionary<PageId, int> Pages = new();
    }

    private sealed class PoolFrame
    {
        public PageId PageId = PageId.Invalid;
        public int PinCount;
        public long Generation;
        public bool Referenced;
        public bool IsDirty;
        public long DirtyTransactionId;
        public readonly byte[] Buffer = new byte[PageSizeConst];
        // per-frame RW lock — Pin{Read|Write} 中の他スレッドからのページバッファ
        // 並行アクセスを排他する。同一ページ上の異なるレコードを別 tx (= 別スレッド) が
        // 同時更新するケースをサポート (Postgres SI 風)。
        // SupportsRecursion: 同 tx 内で同一ページに対し PinForRead → PinForWrite を
        // 連続させる経路 (例: header ページ更新) があるため、recursion を許可する。
        public readonly ReaderWriterLockSlim FrameLock = new(LockRecursionPolicy.SupportsRecursion);
    }
}

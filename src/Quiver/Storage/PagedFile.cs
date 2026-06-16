using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Quiver.Core;
using Quiver.Telemetry;
using Quiver.Storage.Wal;

namespace Quiver.Storage;

/// <summary>
/// MemoryMappedFile + Clock バッファプールによる IPagedFile 実装。
/// ページ 0 をメタデータページとして使用し、Free List と総ページ数を管理する。
/// </summary>
internal sealed class PagedFile : IPagedFile
{
    public const int PageSizeConst = 8192;
    public const int BodySize = PageSizeConst - PageHeader.Size;
    private const int DefaultPoolCapacity = 256;
    private const long GrowthBytes = 64 * 1024 * 1024; // 64 MB 単位で拡張

    // メタページ (page 0) の body 内オフセット
    private const int MetaOffsetFirstFree = 0;  // int64: Free List 先頭 PageId (-1 = 空)
    private const int MetaOffsetPageCount = 8;  // int64: 論理割り当て済みページ数
    private static readonly PageId MetaPageId = new(0);

    private readonly string _path;
    private readonly int _poolCapacity;
    // _poolLock は buffer pool 操作と MMF アクセス全体を保護する
    private readonly object _poolLock = new();

    private FileStream _fileStream;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _viewAccessor;
    // 論理的に割り当て済みのページ数 (メタページの PageCount と同期)
    private long _logicalPageCount;

    private readonly PoolFrame[] _frames;
    private readonly Dictionary<PageId, int> _pageToFrame;
    private int _clockHand;
    private bool _disposed;
    private byte? _walFileKind;
    // FT-15: WAL を先行フラッシュ (write-ahead) するために保持する。EnableWalLogging で配線。
    private IWriteAheadLog? _wal;
    // OB-2: dotnet-counters の buffer-pool-size-bytes gauge へ提供する provider 登録ハンドル。
    private readonly IDisposable _bufferPoolSizeRegistration;

    int IPagedFile.PageSize => PageSizeConst;
    public long PageCount => Volatile.Read(ref _logicalPageCount);
    public string Path => _path;

    public PagedFile(string path, int poolCapacity = DefaultPoolCapacity)
    {
        _path = path;
        _poolCapacity = poolCapacity;
        _frames = new PoolFrame[poolCapacity];
        _pageToFrame = new Dictionary<PageId, int>(poolCapacity);
        for (int i = 0; i < poolCapacity; i++)
            _frames[i] = new PoolFrame();

        // OB-2: 本 PagedFile が保有するバッファプールサイズを EventCounters の gauge に登録する。
        // 複数 PagedFile (data + index 等) が並存しても合算されて 1 つのメトリクスとして出る。
        long bufferPoolBytes = (long)_poolCapacity * PageSizeConst;
        _bufferPoolSizeRegistration =
            QuiverEventSource.Log.RegisterBufferPoolSizeBytesProvider(() => bufferPoolBytes);

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
        lock (_poolLock)
        {
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

    public void FreePage(PageId pageId)
    {
        lock (_poolLock)
        {
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

    public PageReadHandle PinForRead(PageId pageId)
    {
        int frame = GetOrLoadFrame(pageId);
        // FT-26: ハンドル生存中、他スレッドからの書き込みからバッファを保護する。
        // Task B: 検証はロード時に済んでいるので pin ごとの再検証はしない (上記 GetOrLoadFrame 参照)。
        _frames[frame].FrameLock.EnterReadLock();
        return new PageReadHandle(this, pageId, ReadFrameSpan(frame));
    }

    public PageWriteHandle PinForWrite(PageId pageId) => PinForWrite(pageId, WalJournalMode.Full);

    public PageWriteHandle PinForWrite(PageId pageId, WalJournalMode mode)
    {
        int frame = GetOrLoadFrame(pageId);
        // FT-26: ハンドル生存中、他スレッドからの読み書きを排他する。
        _frames[frame].FrameLock.EnterWriteLock();
        try
        {
            Span<byte> raw = ReadFrameSpan(frame);
            // Task B: 検証はロード時に済んでいるので pin ごとの再検証はしない (GetOrLoadFrame 参照)。
            // FT-15: この書き込みトランザクション内で本ページを初めて pin する時点の内容を
            // before-image として捕捉する。caller がまだ変更していないこの瞬間が唯一の機会。
            // frame は pin 済みなので evict されず、span は安定している。
            // FTS-7: journaling モードを記録し (spec: 07_fulltext.md#ft-journaling)、有効モードが Full のときのみ CLR を捕捉する
            //   (RedoOnly/Suppressed の FT ページは before-image を出さない)。
            if (_walFileKind is byte fileKind)
            {
                var eff = WalPageContext.SetJournalMode(fileKind, pageId.Value, mode);
                if (eff == WalJournalMode.Full)
                    WalPageContext.CaptureBeforeImage(fileKind, pageId.Value, raw);
            }
            return new PageWriteHandle(this, pageId, raw);
        }
        catch
        {
            _frames[frame].FrameLock.ExitWriteLock();
            lock (_poolLock)
            {
                if (_pageToFrame.TryGetValue(pageId, out int f))
                    Interlocked.Decrement(ref _frames[f].PinCount);
            }
            throw;
        }
    }

    void IPagedFile.Unpin(PageId pageId)
    {
        int? lockedFrame = null;
        lock (_poolLock)
        {
            if (_pageToFrame.TryGetValue(pageId, out int frame))
            {
                Interlocked.Decrement(ref _frames[frame].PinCount);
                lockedFrame = frame;
            }
        }
        if (lockedFrame is int f)
            _frames[f].FrameLock.ExitReadLock();
    }

    void IPagedFile.UnpinDirty(PageId pageId, long lsn)
    {
        int? lockedFrame = null;
        lock (_poolLock)
        {
            if (!_pageToFrame.TryGetValue(pageId, out int frame)) return;

            // WAL ログ書き込み前にヘッダの LSN とチェックサムを更新し、ページイメージを有効化する。
            PageHeader.UpdateLsnAndChecksum(_frames[frame].Buffer.AsSpan(), lsn);

            // WAL-first: クラッシュリカバリでコミット済み書き込みを再生できるよう、ページイメージをログに残す。
            if (_walFileKind is byte fileKind)
                WalPageContext.LogPageImage(fileKind, pageId.Value, _frames[frame].Buffer.AsSpan(0, PageSizeConst));

            _frames[frame].IsDirty = true;
            Interlocked.Decrement(ref _frames[frame].PinCount);
            lockedFrame = frame;
        }
        if (lockedFrame is int f)
            _frames[f].FrameLock.ExitWriteLock();
    }

    public void EnableWalLogging(byte fileKind, IWriteAheadLog wal)
    {
        _walFileKind = fileKind;
        _wal = wal;
    }

    /// <summary>
    /// FT-18: WAL 参照のみ配線する (fileKind は付けない)。物理 PageImage / before-image
    /// は出さないが、buffer-pool eviction やフラッシュ前に WAL を write-ahead でフラッシュする
    /// ので、ページが OS-MMF に到達する前に対応する論理ログ (IndexMutation 等) が durable に
    /// なっていることを保証できる。B+Tree インデックスファイル用 — 物理ロギングのコスト
    /// (split 1 回で 3 ページ ×8KB) を回避しつつデータファイルと同じ write-ahead 順序を効かせる。
    /// </summary>
    public void EnableWalFlushOnly(IWriteAheadLog wal)
    {
        _wal = wal;
    }

    public void WritePageForRecovery(PageId pageId, ReadOnlySpan<byte> pageBytes)
    {
        if (pageBytes.Length != PageSizeConst) return;
        lock (_poolLock)
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
            if (_pageToFrame.TryGetValue(pageId, out int frame))
            {
                pageBytes.CopyTo(_frames[frame].Buffer.AsSpan());
                _frames[frame].IsDirty = false;
            }
        }
    }

    public void Flush()
    {
        lock (_poolLock)
        {
            FlushDirtyFramesLocked();
            _viewAccessor?.Flush();
        }
        _fileStream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// OP-5: ページファイルを <paramref name="newPageCount"/> へ物理 truncate する。
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
        lock (_poolLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
                _pageToFrame.Remove(f.PageId);
                f.PageId = PageId.Invalid;
                f.IsDirty = false;
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

    private int GetOrLoadFrame(PageId pageId)
    {
        lock (_poolLock)
        {
            if (_pageToFrame.TryGetValue(pageId, out int existing))
            {
                _frames[existing].Referenced = true;
                Interlocked.Increment(ref _frames[existing].PinCount);
                // OB-1: バッファプールヒット計上 (lock 内で counter add は軽量)。
                QuiverTelemetry.BufferPoolHits.Add(1);
                QuiverEventSource.Log.BufferPoolHit();
                return existing;
            }

            int victim = FindVictim();
            // OB-2: 既存ページを追い出してから新規ロードする場合のみ eviction としてカウント。
            // 起動直後の空フレーム埋めはミスにはなるが eviction ではない。
            bool wasEviction = _frames[victim].PageId.IsValid;
            EvictFrame(victim);

            _frames[victim].PageId = pageId;
            MmfReadPage(pageId, _frames[victim].Buffer);
            // Task B: checksum / magic / pageId の検証は disk→frame ロード時 (= ここ) のみ行う。
            // 常駐フレームの pin ごとに全ページ CRC を再計算するのは冗長 (RAM 上の内容は disk 破損に
            // 晒されず、書込は UnpinDirty で checksum を更新し FrameLock が read/write pin を排他する)。
            // 破損ページの早期検出はロード時で十分 (StorageTests.CorruptMagic / crash contract が担保)。
            PageHeader.Validate(ReadFrameSpan(victim), pageId);
            _frames[victim].Referenced = true;
            _frames[victim].IsDirty = false;
            _pageToFrame[pageId] = victim;
            Interlocked.Increment(ref _frames[victim].PinCount);
            // OB-1: バッファプールミス (eviction + page-in 発生)。
            QuiverTelemetry.BufferPoolMisses.Add(1);
            QuiverEventSource.Log.BufferPoolMiss();
            if (wasEviction) QuiverEventSource.Log.BufferPoolEviction();
            return victim;
        }
    }

    // Clock (Second-Chance) アルゴリズム。_poolLock 保持下で呼び出す。
    private int FindVictim()
    {
        while (true)
        {
            ref PoolFrame f = ref _frames[_clockHand];
            if (f.PinCount == 0)
            {
                if (!f.Referenced)
                {
                    int victim = _clockHand;
                    _clockHand = (_clockHand + 1) % _poolCapacity;
                    return victim;
                }
                f.Referenced = false;
            }
            _clockHand = (_clockHand + 1) % _poolCapacity;
        }
    }

    // _poolLock 保持下で呼び出す。
    private void EvictFrame(int frame)
    {
        ref PoolFrame f = ref _frames[frame];
        if (f.PageId.IsValid)
        {
            if (f.IsDirty)
            {
                // FT-15: steal ポリシー下では未コミットトランザクションのダーティページが
                // ここでデータファイルへ漏れうる。クラッシュ時に巻き戻せるよう、ページを
                // データファイルへ書く前にその before-image (CLR) が WAL に durable で
                // あることを保証する (write-ahead 順序)。
                FlushWalBeforeDataWrite();
                MmfWritePage(f.PageId, f.Buffer);
            }
            _pageToFrame.Remove(f.PageId);
            f.PageId = PageId.Invalid;
            f.IsDirty = false;
        }
    }

    // FT-15: ダーティページをデータファイルへ書き出す前に WAL を末尾まで先行フラッシュする。
    // FlushTo は flushedLsn が既に追いついていれば no-op なので、同一フラッシュ波の中で
    // 余計な fsync は発生しない。
    private void FlushWalBeforeDataWrite()
    {
        if (_wal is { } wal)
            wal.FlushTo(wal.CurrentLsn);
    }

    private Span<byte> ReadFrameSpan(int frame) => new Span<byte>(_frames[frame].Buffer);

    // ------------------------------------------------------------------
    // MMF アクセスヘルパー (いずれも _poolLock 保持下で呼び出す)
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
        if (_pageToFrame.TryGetValue(pageId, out int frame))
        {
            buffer.AsSpan(0, PageSizeConst).CopyTo(_frames[frame].Buffer);
            _frames[frame].IsDirty = false;
        }
    }

    // ダーティなフレームを全て MMF に書き出す。_poolLock 保持下で呼び出す。
    private void FlushDirtyFramesLocked()
    {
        bool anyDirty = false;
        for (int i = 0; i < _poolCapacity; i++)
        {
            if (_frames[i].IsDirty && _frames[i].PageId.IsValid) { anyDirty = true; break; }
        }
        // FT-15: データページを書き出す前に WAL を先行フラッシュする (checkpoint / Flush /
        // ファイル拡張時の remap 経路も含む write-ahead 順序)。
        if (anyDirty) FlushWalBeforeDataWrite();

        for (int i = 0; i < _poolCapacity; i++)
        {
            ref PoolFrame f = ref _frames[i];
            if (f.IsDirty && f.PageId.IsValid)
            {
                MmfWritePage(f.PageId, f.Buffer);
                f.IsDirty = false;
            }
        }
    }

    // ------------------------------------------------------------------
    // ファイルサイズ管理
    // ------------------------------------------------------------------

    // MMF マップ前(コンストラクタ内)に使用。
    private void EnsureRawFileSize(long pageCount)
    {
        long required = pageCount * PageSizeConst;
        if (_fileStream.Length < required)
        {
            long grown = ((required + GrowthBytes - 1) / GrowthBytes) * GrowthBytes;
            _fileStream.SetLength(grown);
        }
    }

    // _poolLock 保持下で呼び出す。ファイル拡張時に MMF を再マップする。
    private void EnsureFileSizeAndRemapLocked(long pageCount)
    {
        long required = pageCount * PageSizeConst;
        if (_fileStream.Length < required)
        {
            FlushDirtyFramesLocked();
            long grown = ((required + GrowthBytes - 1) / GrowthBytes) * GrowthBytes;
            _viewAccessor?.Flush();
            _viewAccessor?.Dispose();
            _mmf?.Dispose();
            _mmf = null;
            _viewAccessor = null;
            _fileStream.SetLength(grown);
            MapFile();
        }
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
        if (_disposed) return;
        _disposed = true;
        Flush();
        _viewAccessor?.Dispose();
        _mmf?.Dispose();
        _fileStream.Dispose();
        // OB-2: dotnet-counters の gauge プロバイダから抜ける。
        _bufferPoolSizeRegistration.Dispose();
    }

    private sealed class PoolFrame
    {
        public PageId PageId = PageId.Invalid;
        public int PinCount;
        public bool Referenced;
        public bool IsDirty;
        public readonly byte[] Buffer = new byte[PageSizeConst];
        // FT-26: per-frame RW lock — Pin{Read|Write} 中の他スレッドからのページバッファ
        // 並行アクセスを排他する。NodeStore.Allocate などで「同一ページ上の異なるレコード」を
        // 別 tx (= 別スレッド) が同時更新するケースをサポート (Postgres SI 風)。
        // SupportsRecursion: NodeStore は同 tx 内で同一ページに対し PinForRead → PinForWrite を
        // 連続させる経路 (例: header ページ更新) があるため、recursion を許可する。
        public readonly ReaderWriterLockSlim FrameLock = new(LockRecursionPolicy.SupportsRecursion);
    }
}

using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using GraphDb.Engine.Core;

namespace GraphDb.Engine.Storage;

/// <summary>
/// MemoryMappedFile + Clock バッファプールによる IPagedFile 実装。
/// ページ 0 をメタデータページとして使用し、Free List と総ページ数を管理する。
/// </summary>
public sealed class PagedFile : IPagedFile
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

    int IPagedFile.PageSize => PageSizeConst;
    public long PageCount => Volatile.Read(ref _logicalPageCount);

    public PagedFile(string path, int poolCapacity = DefaultPoolCapacity)
    {
        _path = path;
        _poolCapacity = poolCapacity;
        _frames = new PoolFrame[poolCapacity];
        _pageToFrame = new Dictionary<PageId, int>(poolCapacity);
        for (int i = 0; i < poolCapacity; i++)
            _frames[i] = new PoolFrame();

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
        Span<byte> raw = ReadFrameSpan(frame);
        PageHeader.Validate(raw, pageId);
        return new PageReadHandle(this, pageId, raw);
    }

    public PageWriteHandle PinForWrite(PageId pageId)
    {
        int frame = GetOrLoadFrame(pageId);
        Span<byte> raw = ReadFrameSpan(frame);
        PageHeader.Validate(raw, pageId);
        return new PageWriteHandle(this, pageId, raw);
    }

    void IPagedFile.Unpin(PageId pageId)
    {
        lock (_poolLock)
        {
            if (_pageToFrame.TryGetValue(pageId, out int frame))
                Interlocked.Decrement(ref _frames[frame].PinCount);
        }
    }

    void IPagedFile.UnpinDirty(PageId pageId, long lsn)
    {
        lock (_poolLock)
        {
            if (_pageToFrame.TryGetValue(pageId, out int frame))
            {
                // 書き込み後にチェックサムと LSN をヘッダに反映
                PageHeader.UpdateLsnAndChecksum(_frames[frame].Buffer.AsSpan(), lsn);
                _frames[frame].IsDirty = true;
                Interlocked.Decrement(ref _frames[frame].PinCount);
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
                return existing;
            }

            int victim = FindVictim();
            EvictFrame(victim);

            _frames[victim].PageId = pageId;
            MmfReadPage(pageId, _frames[victim].Buffer);
            _frames[victim].Referenced = true;
            _frames[victim].IsDirty = false;
            _pageToFrame[pageId] = victim;
            Interlocked.Increment(ref _frames[victim].PinCount);
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
                MmfWritePage(f.PageId, f.Buffer);
            _pageToFrame.Remove(f.PageId);
            f.PageId = PageId.Invalid;
            f.IsDirty = false;
        }
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
    }

    private sealed class PoolFrame
    {
        public PageId PageId = PageId.Invalid;
        public int PinCount;
        public bool Referenced;
        public bool IsDirty;
        public readonly byte[] Buffer = new byte[PageSizeConst];
    }
}

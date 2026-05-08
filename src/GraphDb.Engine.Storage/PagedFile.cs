using System.IO.MemoryMappedFiles;
using GraphDb.Engine.Core;

namespace GraphDb.Engine.Storage;

/// <summary>
/// MemoryMappedFile + Clock バッファプールによる IPagedFile 実装。
/// </summary>
public sealed class PagedFile : IPagedFile
{
    public const int PageSizeConst = 8192;
    public const int BodySize = PageSizeConst - PageHeader.Size;
    private const int DefaultPoolCapacity = 256;
    private const long GrowthBytes = 64 * 1024 * 1024;

    private readonly string _path;
    private readonly int _poolCapacity;
    private readonly object _poolLock = new();

    private FileStream _fileStream;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _viewAccessor;
    private long _filePageCount;

    private readonly PoolFrame[] _frames;
    private readonly Dictionary<PageId, int> _pageToFrame;
    private int _clockHand;
    private bool _disposed;

    int IPagedFile.PageSize => PageSizeConst;
    public long PageCount => Volatile.Read(ref _filePageCount);

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
            EnsureFileSize(1);
            _filePageCount = 1;
        }
        else
        {
            _filePageCount = _fileStream.Length / PageSizeConst;
        }

        MapFile();
    }

    public PageId AllocatePage(PageKind kind) => throw new NotImplementedException();
    public void FreePage(PageId pageId) => throw new NotImplementedException();

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

    void IPagedFile.UnpinDirty(PageId pageId)
    {
        lock (_poolLock)
        {
            if (_pageToFrame.TryGetValue(pageId, out int frame))
            {
                _frames[frame].IsDirty = true;
                Interlocked.Decrement(ref _frames[frame].PinCount);
            }
        }
    }

    public void Flush()
    {
        lock (_poolLock)
        {
            for (int i = 0; i < _poolCapacity; i++)
                _frames[i].IsDirty = false;
        }
        _viewAccessor?.Flush();
        _fileStream.Flush(flushToDisk: true);
    }

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
            _frames[victim].Referenced = true;
            _frames[victim].IsDirty = false;
            _pageToFrame[pageId] = victim;
            Interlocked.Increment(ref _frames[victim].PinCount);
            return victim;
        }
    }

    // Clock (Second-Chance) アルゴリズム
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

    private void EvictFrame(int frame)
    {
        ref PoolFrame f = ref _frames[frame];
        if (f.PageId.IsValid)
        {
            _pageToFrame.Remove(f.PageId);
            f.PageId = PageId.Invalid;
        }
    }

    private Span<byte> ReadFrameSpan(int frame)
    {
        long offset = _frames[frame].PageId.Value * PageSizeConst;
        byte[] buf = new byte[PageSizeConst];
        _viewAccessor!.ReadArray(offset, buf, 0, PageSizeConst);
        return buf;
    }

    private void EnsureFileSize(long pageCount)
    {
        long required = pageCount * PageSizeConst;
        if (_fileStream.Length < required)
        {
            // 64MB 単位で切り上げ
            long grown = ((required + GrowthBytes - 1) / GrowthBytes) * GrowthBytes;
            _fileStream.SetLength(grown);
        }
    }

    private void MapFile()
    {
        long capacity = Math.Max(_fileStream.Length, PageSizeConst);
        _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        _viewAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
    }

    private void RemapFile()
    {
        _viewAccessor?.Flush();
        _viewAccessor?.Dispose();
        _mmf?.Dispose();
        MapFile();
    }

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
    }
}

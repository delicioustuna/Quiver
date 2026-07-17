using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage.Wal;

namespace Quiver.Storage;

/// <summary>
/// ページをすべてプロセス内メモリへ保持する <see cref="IPagedFile"/> 実装。
/// ファイルシステムへの読み書きは行わず、破棄時に全ページを失う。
/// </summary>
internal sealed class InMemoryPagedFile : IPagedFile
{
    private const int MetaOffsetFirstFree = 0;
    private const int MetaOffsetPageCount = 8;

    private readonly List<byte[]> _pages = new();
    private readonly List<ReaderWriterLockSlim> _pageLocks = new();
    private readonly object _allocLock = new();
    private long _freeListHead = -1;
    private long _logicalPageCount = 1;
    private byte? _walFileKind;
    private IWriteAheadLog? _wal;
    private bool _disposed;

    int IPagedFile.PageSize => PagedFile.PageSizeConst;
    public long PageCount => Volatile.Read(ref _logicalPageCount);
    public string Path => string.Empty;

    public InMemoryPagedFile()
    {
        var meta = new byte[PagedFile.PageSizeConst];
        _pages.Add(meta);
        _pageLocks.Add(new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion));
        WriteMetaPage();
    }

    public PageId AllocatePage(PageKind kind)
    {
        lock (_allocLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            PageId pageId;
            byte[] page;
            if (_freeListHead >= 0)
            {
                pageId = new PageId(_freeListHead);
                page = _pages[(int)pageId.Value];
                _freeListHead = BinaryPrimitives.ReadInt64LittleEndian(
                    page.AsSpan(PageHeader.Size));
                page.AsSpan().Clear();
            }
            else
            {
                pageId = new PageId(_logicalPageCount);
                page = new byte[PagedFile.PageSizeConst];
                _pages.Add(page);
                _pageLocks.Add(new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion));
                _logicalPageCount++;
            }

            PageHeader.Write(page, pageId, kind, lsn: 0);
            WriteMetaPage();
            return pageId;
        }
    }

    public void FreePage(PageId pageId)
    {
        lock (_allocLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateAllocatedPageId(pageId);
            if (pageId.Value == 0)
                throw new ArgumentOutOfRangeException(nameof(pageId), "メタページは解放できません。");

            var pageLock = _pageLocks[(int)pageId.Value];
            pageLock.EnterWriteLock();
            try
            {
                var page = _pages[(int)pageId.Value];
                page.AsSpan().Clear();
                BinaryPrimitives.WriteInt64LittleEndian(
                    page.AsSpan(PageHeader.Size), _freeListHead);
                PageHeader.Write(page, pageId, PageKind.Free, lsn: 0);
                _freeListHead = pageId.Value;
                WriteMetaPage();
            }
            finally
            {
                pageLock.ExitWriteLock();
            }
        }
    }

    public PageReadHandle PinForRead(PageId pageId)
    {
        byte[] page;
        ReaderWriterLockSlim pageLock;
        lock (_allocLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateAllocatedPageId(pageId);
            page = _pages[(int)pageId.Value];
            pageLock = _pageLocks[(int)pageId.Value];
            pageLock.EnterReadLock();
        }

        return new PageReadHandle(this, pageId, page);
    }

    public PageWriteHandle PinForWrite(PageId pageId)
    {
        byte[] page;
        ReaderWriterLockSlim pageLock;
        lock (_allocLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateAllocatedPageId(pageId);
            page = _pages[(int)pageId.Value];
            pageLock = _pageLocks[(int)pageId.Value];
            pageLock.EnterWriteLock();
        }

        try
        {
            if (_walFileKind is byte fileKind)
            {
                _wal?.ActiveWriteSet?.CaptureBeforeImage(fileKind, pageId.Value, page);
            }

            return new PageWriteHandle(this, pageId, page);
        }
        catch
        {
            pageLock.ExitWriteLock();
            throw;
        }
    }

    public void Unpin(PageId pageId)
        => _pageLocks[(int)pageId.Value].ExitReadLock();

    public void UnpinDirty(PageId pageId, long lsn)
    {
        var pageLock = _pageLocks[(int)pageId.Value];
        try
        {
            var page = _pages[(int)pageId.Value];
            PageHeader.UpdateLsnAndChecksum(page, lsn);
            if (_walFileKind is byte fileKind)
                _wal?.ActiveWriteSet?.LogPageImage(fileKind, pageId.Value, page);
        }
        finally
        {
            pageLock.ExitWriteLock();
        }
    }

    public void Flush()
    {
        // RAM 専用のため永続化先はない。
    }

    public void EnableWalLogging(byte fileKind, IWriteAheadLog wal)
    {
        _walFileKind = fileKind;
        _wal = wal;
    }

    public void WritePageForRecovery(PageId pageId, ReadOnlySpan<byte> pageBytes)
    {
        if (pageBytes.Length != PagedFile.PageSizeConst)
            return;

        lock (_allocLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsurePageExists(pageId.Value);
            pageBytes.CopyTo(_pages[(int)pageId.Value]);

            if (pageId.Value == 0)
            {
                var body = _pages[0].AsSpan(PageHeader.Size);
                _freeListHead = BinaryPrimitives.ReadInt64LittleEndian(body[MetaOffsetFirstFree..]);
                _logicalPageCount = BinaryPrimitives.ReadInt64LittleEndian(body[MetaOffsetPageCount..]);
            }
            else
            {
                _logicalPageCount = Math.Max(_logicalPageCount, pageId.Value + 1);
            }
        }
    }

    public void Truncate(long newPageCount)
    {
        if (newPageCount < 1)
            throw new ArgumentOutOfRangeException(
                nameof(newPageCount), "メタページを保持するため 1 以上を指定してください。");

        lock (_allocLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (newPageCount >= _logicalPageCount)
                return;

            for (int i = _pageLocks.Count - 1; i >= newPageCount; i--)
            {
                _pageLocks[i].Dispose();
                _pageLocks.RemoveAt(i);
                _pages.RemoveAt(i);
            }

            _logicalPageCount = newPageCount;
            _freeListHead = -1;
            WriteMetaPage();
        }
    }

    public void Dispose()
    {
        lock (_allocLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var pageLock in _pageLocks)
                pageLock.Dispose();
            _pageLocks.Clear();
            _pages.Clear();
            _wal = null;
        }
    }

    private void EnsurePageExists(long pageId)
    {
        while (_pages.Count <= pageId)
        {
            _pages.Add(new byte[PagedFile.PageSizeConst]);
            _pageLocks.Add(new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion));
        }
    }

    private void ValidateAllocatedPageId(PageId pageId)
    {
        if (pageId.Value < 0 || pageId.Value >= _logicalPageCount)
            throw new ArgumentOutOfRangeException(
                nameof(pageId), $"ページ {pageId.Value} は割り当てられていません。");
    }

    private void WriteMetaPage()
    {
        var meta = _pages[0].AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(
            meta[(PageHeader.Size + MetaOffsetFirstFree)..], _freeListHead);
        BinaryPrimitives.WriteInt64LittleEndian(
            meta[(PageHeader.Size + MetaOffsetPageCount)..], _logicalPageCount);
        PageHeader.Write(meta, new PageId(0), PageKind.Header, lsn: 0);
    }
}

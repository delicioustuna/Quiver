namespace Quiver.Storage;

public sealed class PageManager : IPageManager
{
    private readonly List<IPagedFile> _files = new();
    private readonly object _filesLock = new();
    private bool _disposed;

    public IPagedFile OpenOrCreate(string path, PageKind defaultKind)
    {
        var file = new PagedFile(path);
        lock (_filesLock) _files.Add(file);
        return file;
    }

    public void FlushAll()
    {
        IPagedFile[] snapshot;
        lock (_filesLock) snapshot = _files.ToArray();
        foreach (var f in snapshot)
            f.Flush();
    }

    /// <summary>
    /// Detach <paramref name="file"/> from manager-owned lifecycle. The caller
    /// becomes responsible for disposal. PW-14 uses this when compact reopens
    /// the adjacency data file with fresh contents — the old handle is dropped
    /// before <see cref="PagedFile"/>'s exclusive lock blocks reuse of the path.
    /// </summary>
    public void Drop(IPagedFile file)
    {
        lock (_filesLock) _files.Remove(file);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IPagedFile[] snapshot;
        lock (_filesLock) snapshot = _files.ToArray();
        foreach (var f in snapshot)
            f.Dispose();
    }
}

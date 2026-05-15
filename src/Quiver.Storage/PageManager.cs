namespace Quiver.Storage;

public sealed class PageManager : IPageManager
{
    private readonly List<IPagedFile> _files = new();
    private bool _disposed;

    public IPagedFile OpenOrCreate(string path, PageKind defaultKind)
    {
        var file = new PagedFile(path);
        _files.Add(file);
        return file;
    }

    /// <summary>
    /// Detach <paramref name="file"/> from manager-owned lifecycle. The caller
    /// becomes responsible for disposal. PW-14 uses this when compact reopens
    /// the adjacency data file with fresh contents — the old handle is dropped
    /// before <see cref="PagedFile"/>'s exclusive lock blocks reuse of the path.
    /// </summary>
    public void Drop(IPagedFile file) => _files.Remove(file);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var f in _files)
            f.Dispose();
    }
}

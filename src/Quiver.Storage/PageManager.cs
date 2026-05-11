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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var f in _files)
            f.Dispose();
    }
}

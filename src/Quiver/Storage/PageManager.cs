namespace Quiver.Storage;

internal sealed class PageManager : IPageManager
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

    /// <summary>
    /// ARCH-4: 外部 (SingleFileContainer) が生成済みの <see cref="IPagedFile"/> を管理下に取り込み、
    /// <see cref="FlushAll"/> / <see cref="Files"/> / <see cref="Dispose"/> の対象にする。
    /// 単一ファイルコンテナの物理ファイルを checkpoint / snapshot 経路に乗せるのに使う。
    /// </summary>
    public void Adopt(IPagedFile file)
    {
        lock (_filesLock) _files.Add(file);
    }

    public void FlushAll()
    {
        IPagedFile[] snapshot;
        lock (_filesLock) snapshot = _files.ToArray();
        foreach (var f in snapshot)
            f.Flush();
    }

    public IReadOnlyList<IPagedFile> Files
    {
        get
        {
            lock (_filesLock) return _files.ToArray();
        }
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

using Yatagarasu.Core;

namespace Yatagarasu.Storage;

/// <summary>
/// 読み取り専用ページハンドル。Dispose で Unpin。
/// パターンベース using のため IDisposable は実装しない。
/// </summary>
internal ref struct PageReadHandle
{
    private readonly IPagedFile? _file;
    private readonly PageId _pageId;
    private readonly ReadOnlySpan<byte> _raw;
    private readonly ReadPageLease _lease;
    private bool _released;

    public PageId PageId => _pageId;
    public ReadOnlySpan<byte> Raw => _raw;
    public ReadOnlySpan<byte> Data => _raw[PageHeader.Size..];

    internal PageReadHandle(IPagedFile file, ReadPageLease lease, ReadOnlySpan<byte> raw)
    {
        _file = file;
        _pageId = lease.PageId;
        _raw = raw;
        _lease = lease;
        _released = false;
    }

    internal PageReadHandle(PageId pageId, byte[] snapshot)
    {
        _file = null;
        _pageId = pageId;
        _raw = snapshot;
        _lease = new ReadPageLease(pageId, -1, 0);
        _released = false;
    }

    public void Dispose()
    {
        if (_released) throw new InvalidOperationException("Read page handle has already been released.");
        if (_file is null) _lease.ClaimRelease();
        else _file.ReleaseRead(_lease);
        _released = true;
    }
}

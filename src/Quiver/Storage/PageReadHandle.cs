using Quiver.Core;

namespace Quiver.Storage;

/// <summary>
/// 読み取り専用ページハンドル。Dispose で Unpin。
/// パターンベース using のため IDisposable は実装しない。
/// </summary>
internal readonly ref struct PageReadHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private readonly ReadOnlySpan<byte> _raw;

    public PageId PageId => _pageId;
    public ReadOnlySpan<byte> Raw => _raw;
    public ReadOnlySpan<byte> Data => _raw[PageHeader.Size..];

    internal PageReadHandle(IPagedFile file, PageId pageId, ReadOnlySpan<byte> raw)
    {
        _file = file;
        _pageId = pageId;
        _raw = raw;
    }

    public void Dispose() => _file.Unpin(_pageId);
}

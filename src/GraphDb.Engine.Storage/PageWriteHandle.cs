using GraphDb.Engine.Core;

namespace GraphDb.Engine.Storage;

/// <summary>
/// 読み書きページハンドル。Dispose 時に dirty マークが付く。
/// パターンベース using のため IDisposable は実装しない。
/// </summary>
public ref struct PageWriteHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private readonly Span<byte> _raw;

    public PageId PageId => _pageId;
    public Span<byte> Raw => _raw;
    public Span<byte> Data => _raw[PageHeader.Size..];
    public long Lsn { get; set; }

    internal PageWriteHandle(IPagedFile file, PageId pageId, Span<byte> raw)
    {
        _file = file;
        _pageId = pageId;
        _raw = raw;
        Lsn = 0;
    }

    public void Dispose() => _file.UnpinDirty(_pageId);
}

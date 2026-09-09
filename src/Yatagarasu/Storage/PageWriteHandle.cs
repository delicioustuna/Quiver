using Yatagarasu.Core;

namespace Yatagarasu.Storage;

/// <summary>
/// 読み書きページハンドル。Dispose 時に dirty マークが付く。
/// パターンベース using のため IDisposable は実装しない。
/// </summary>
internal ref struct PageWriteHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private readonly Span<byte> _raw;
    private readonly WritePageLease _lease;
    private bool _released;

    public PageId PageId => _pageId;
    public Span<byte> Raw => _raw;
    public Span<byte> Data => _raw[PageHeader.Size..];
    public long Lsn { get; set; }

    internal PageWriteHandle(IPagedFile file, WritePageLease lease, Span<byte> raw)
    {
        _file = file;
        _pageId = lease.PageId;
        _raw = raw;
        _lease = lease;
        _released = false;
        Lsn = 0;
    }

    public void Dispose()
    {
        EnsureOwned();
        _released = true;
        _file.ReleaseWriteDirty(_lease, Lsn);
    }

    internal PageWriteHandle Transfer()
    {
        EnsureOwned();
        var owner = this;
        _released = true;
        return owner;
    }

    private readonly void EnsureOwned()
    {
        if (_released) throw new InvalidOperationException("Write page handle has already been released or transferred.");
    }

    /// <summary>バッファを変更していない検証失敗時に限り、書き込み用ページの固定を、変更済みにせず解除する。</summary>
    internal void ReleaseUnchanged()
    {
        EnsureOwned();
        _released = true;
        _file.ReleaseWriteUnchanged(_lease);
    }
}

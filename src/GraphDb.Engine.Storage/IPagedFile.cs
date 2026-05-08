using GraphDb.Engine.Core;

namespace GraphDb.Engine.Storage;

/// <summary>
/// 単一データファイルのページ単位アクセスを提供する。
/// </summary>
public interface IPagedFile : IDisposable
{
    int PageSize { get; }
    long PageCount { get; }

    PageId AllocatePage(PageKind kind);
    void FreePage(PageId pageId);
    PageReadHandle PinForRead(PageId pageId);
    PageWriteHandle PinForWrite(PageId pageId);

    /// <summary>内部用: 読み取りピン解除。</summary>
    internal void Unpin(PageId pageId);

    /// <summary>内部用: 書き込みピン解除(dirty マーク)。LSN を受け取りヘッダを更新する。</summary>
    internal void UnpinDirty(PageId pageId, long lsn);

    void Flush();
}

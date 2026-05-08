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

    void Unpin(PageId pageId);
    void UnpinDirty(PageId pageId, long lsn);

    void Flush();
}

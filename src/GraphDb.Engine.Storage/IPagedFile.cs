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

    /// <summary>Register this file for WAL page image logging with the given file kind byte.</summary>
    void EnableWalLogging(byte fileKind);

    /// <summary>
    /// Write raw page bytes directly, expanding the file if needed.
    /// Used by RecoveryManager during WAL replay. Bypasses the buffer pool.
    /// </summary>
    void WritePageForRecovery(PageId pageId, ReadOnlySpan<byte> pageBytes);
}

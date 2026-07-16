using Quiver.Core;

namespace Quiver.Storage.Records;

internal static class RecordPageMapping
{
    public const int PageBodySize = PagedFile.BodySize;

    public static (PageId pageId, int recordOffset) GetLocation(long id, int recordSize)
    {
        int recordsPerPage = PageBodySize / recordSize;
        long pageIndex = id / recordsPerPage;
        int recordOffset = (int)(id % recordsPerPage) * recordSize;
        return (new PageId(pageIndex), recordOffset);
    }

    public static long GetId(PageId pageId, int recordOffset, int recordSize)
    {
        int recordsPerPage = PageBodySize / recordSize;
        return pageId.Value * recordsPerPage + recordOffset / recordSize;
    }
}

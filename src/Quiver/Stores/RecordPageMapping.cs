using Quiver.Core;

namespace Quiver.Stores;

internal static class RecordPageMapping
{
    private const int PageSize = 8192;
    private const int PageHeaderSize = 32;
    public const int PageBodySize = PageSize - PageHeaderSize; // 8160

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

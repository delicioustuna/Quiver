using System.Buffers.Binary;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

internal readonly record struct EdgeDeltaHead(
    PageId FirstPageId,
    PageId LastPageId,
    long EntryCount)
{
    public static EdgeDeltaHead Empty { get; } = new(PageId.Invalid, PageId.Invalid, 0);
    public bool IsEmpty => !FirstPageId.IsValid || !LastPageId.IsValid || EntryCount <= 0;
}

/// <summary>
/// vertex sequence と方向から edge delta page chain の head を引く固定 slot sidecar。
/// </summary>
internal sealed class EdgeDeltaHeadStore
{
    private const int RecordSize = 25;
    private const int HeaderFormatOffset = 31;
    private const int OffsetFirstPageId = 0;
    private const int OffsetLastPageId = 8;
    private const int OffsetEntryCount = 16;
    private const int OffsetFormat = 24;

    private static readonly PageId HeaderPageId = new(1);
    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize;

    private readonly IPagedFile _file;

    public EdgeDeltaHeadStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= HeaderPageId.Value)
        {
            _file.AllocatePage(PageKind.Header);
            using var header = _file.PinForWrite(HeaderPageId);
            header.Data[HeaderFormatOffset] = StorageFormatVersion.Current;
        }
        else
        {
            CheckFormatVersion();
        }
    }

    public EdgeDeltaHead Get(VertexId vertexId, Direction direction)
    {
        long slot = SlotIndex(vertexId, direction);
        if (slot < 0)
            return EdgeDeltaHead.Empty;

        var (pageId, offset) = Location(slot);
        if (pageId.Value >= _file.PageCount)
            return EdgeDeltaHead.Empty;

        using var page = _file.PinForRead(pageId);
        ReadOnlySpan<byte> record = page.Data.Slice(offset, RecordSize);
        byte slotFormat = record[OffsetFormat];
        if (slotFormat == 0)
            return EdgeDeltaHead.Empty;
        if (slotFormat != StorageFormatVersion.Current)
            throw new StorageFormatMismatchException(
                "edge-delta-head-slot", slotFormat, StorageFormatVersion.Current);

        long first = BinaryPrimitives.ReadInt64LittleEndian(record[OffsetFirstPageId..]);
        long last = BinaryPrimitives.ReadInt64LittleEndian(record[OffsetLastPageId..]);
        long count = BinaryPrimitives.ReadInt64LittleEndian(record[OffsetEntryCount..]);
        if (first <= 0 || last <= 0 || count <= 0)
            return EdgeDeltaHead.Empty;

        return new EdgeDeltaHead(new PageId(first), new PageId(last), count);
    }

    public void Set(VertexId vertexId, Direction direction, EdgeDeltaHead head)
    {
        long slot = SlotIndex(vertexId, direction);
        if (slot < 0)
            throw new ArgumentOutOfRangeException(nameof(direction));

        var (pageId, offset) = Location(slot);
        EnsurePage(pageId);
        using var page = _file.PinForWrite(pageId);
        Span<byte> record = page.Data.Slice(offset, RecordSize);
        record.Clear();
        if (!head.IsEmpty)
        {
            BinaryPrimitives.WriteInt64LittleEndian(record[OffsetFirstPageId..], head.FirstPageId.Value);
            BinaryPrimitives.WriteInt64LittleEndian(record[OffsetLastPageId..], head.LastPageId.Value);
            BinaryPrimitives.WriteInt64LittleEndian(record[OffsetEntryCount..], head.EntryCount);
            record[OffsetFormat] = StorageFormatVersion.Current;
        }
    }

    public void Reset()
    {
        for (long pageNumber = HeaderPageId.Value + 1; pageNumber < _file.PageCount; pageNumber++)
        {
            using var page = _file.PinForWrite(new PageId(pageNumber));
            page.Data.Clear();
        }
    }

    public void ReloadMeta() => CheckFormatVersion();

    private static long SlotIndex(VertexId vertexId, Direction direction)
    {
        long sequence = vertexId.Sequence;
        if (sequence < 0)
            return -1;

        return direction switch
        {
            Direction.Outgoing => sequence * 2,
            Direction.Incoming => sequence * 2 + 1,
            _ => -1,
        };
    }

    private static (PageId PageId, int Offset) Location(long slot)
        => (new PageId(slot / RecordsPerPage + 2),
            (int)(slot % RecordsPerPage) * RecordSize);

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
        {
            PageId allocated = _file.AllocatePage(PageKind.Header);
            using var page = _file.PinForWrite(allocated);
            page.Data.Clear();
        }
    }

    private void CheckFormatVersion()
    {
        using var header = _file.PinForRead(HeaderPageId);
        byte actual = header.Data[HeaderFormatOffset];
        if (actual != StorageFormatVersion.Current)
            throw new StorageFormatMismatchException(
                "edge-delta-head", actual, StorageFormatVersion.Current);
    }
}

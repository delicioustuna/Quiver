using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

internal readonly record struct RelationshipDeltaHead(
    PageId FirstPageId,
    PageId LastPageId,
    long EntryCount)
{
    public static RelationshipDeltaHead Empty { get; } = new(PageId.Invalid, PageId.Invalid, 0);
    public bool IsEmpty => !FirstPageId.IsValid || !LastPageId.IsValid || EntryCount <= 0;
}

/// <summary>
/// node sequence と方向から relationship delta page chain の head を引く固定 slot sidecar。
/// </summary>
internal sealed class RelationshipDeltaHeadStore
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

    public RelationshipDeltaHeadStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= HeaderPageId.Value)
        {
            _file.AllocatePage(PageKind.Header);
            using var header = _file.PinForWrite(HeaderPageId);
            header.Data[HeaderFormatOffset] = FormatVersion.Current;
        }
        else
        {
            CheckFormatVersion();
        }
    }

    public RelationshipDeltaHead Get(NodeId nodeId, Direction direction)
    {
        long slot = SlotIndex(nodeId, direction);
        if (slot < 0)
            return RelationshipDeltaHead.Empty;

        var (pageId, offset) = Location(slot);
        if (pageId.Value >= _file.PageCount)
            return RelationshipDeltaHead.Empty;

        using var page = _file.PinForRead(pageId);
        ReadOnlySpan<byte> record = page.Data.Slice(offset, RecordSize);
        byte slotFormat = record[OffsetFormat];
        if (slotFormat == 0)
            return RelationshipDeltaHead.Empty;
        if (slotFormat != FormatVersion.Current)
            throw new FormatVersionMismatchException(
                "relationship-delta-head-slot", slotFormat, FormatVersion.Current);

        long first = BinaryPrimitives.ReadInt64LittleEndian(record[OffsetFirstPageId..]);
        long last = BinaryPrimitives.ReadInt64LittleEndian(record[OffsetLastPageId..]);
        long count = BinaryPrimitives.ReadInt64LittleEndian(record[OffsetEntryCount..]);
        if (first <= 0 || last <= 0 || count <= 0)
            return RelationshipDeltaHead.Empty;

        return new RelationshipDeltaHead(new PageId(first), new PageId(last), count);
    }

    public void Set(NodeId nodeId, Direction direction, RelationshipDeltaHead head)
    {
        long slot = SlotIndex(nodeId, direction);
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
            record[OffsetFormat] = FormatVersion.Current;
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

    private static long SlotIndex(NodeId nodeId, Direction direction)
    {
        long sequence = nodeId.Sequence;
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
        if (actual != FormatVersion.Current)
            throw new FormatVersionMismatchException(
                "relationship-delta-head", actual, FormatVersion.Current);
    }
}

using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

internal readonly record struct RelationshipDeltaEntry(
    RelationshipId RelationshipId,
    NodeId OtherNodeId,
    RelationshipTypeId Type,
    byte Flags);

/// <summary>
/// relationship delta entries を node/direction ごとの append-only page chain に保持する。
/// </summary>
internal sealed class PersistentRelationshipDeltaStore
{
    private const int HeaderFormatOffset = 31;
    private const int PageNextOffset = 0;
    private const int PageEntryCountOffset = 8;
    private const int PageReservedOffset = 12;
    private const int DeltaPageHeaderSize = 16;

    private const int EntrySize = 16;
    private const int EntryRelSeqOffset = 0;
    private const int EntryOtherNodeSeqOffset = 6;
    private const int EntryTypeOffset = 12;
    private const int EntryFlagsOffset = 14;

    private static readonly PageId HeaderPageId = new(1);
    internal static int EntriesPerPageForTest => EntriesPerPage;
    private static int EntriesPerPage => (RecordPageMapping.PageBodySize - DeltaPageHeaderSize) / EntrySize;

    private readonly IPagedFile _file;
    private readonly RelationshipDeltaHeadStore _heads;

    public PersistentRelationshipDeltaStore(IPagedFile file, RelationshipDeltaHeadStore heads)
    {
        _file = file;
        _heads = heads;
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

    public void Append(
        NodeId nodeId,
        Direction direction,
        RelationshipId relationshipId,
        NodeId otherNodeId,
        RelationshipTypeId type,
        byte flags = 0)
    {
        if (direction is not (Direction.Outgoing or Direction.Incoming))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (!relationshipId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(relationshipId));

        RelationshipDeltaHead head = _heads.Get(nodeId, direction);
        PageId pageId;
        int entryCount;
        if (head.IsEmpty)
        {
            pageId = AllocateDeltaPage();
            head = new RelationshipDeltaHead(pageId, pageId, 0);
            entryCount = 0;
        }
        else
        {
            pageId = head.LastPageId;
            entryCount = ReadEntryCount(pageId);
            if (entryCount >= EntriesPerPage)
            {
                PageId nextPageId = AllocateDeltaPage();
                using (var previous = _file.PinForWrite(pageId))
                    BinaryPrimitives.WriteInt64LittleEndian(previous.Data[PageNextOffset..], nextPageId.Value);
                pageId = nextPageId;
                head = head with { LastPageId = nextPageId };
                entryCount = 0;
            }
        }

        using (var page = _file.PinForWrite(pageId))
        {
            int offset = DeltaPageHeaderSize + entryCount * EntrySize;
            Span<byte> entry = page.Data.Slice(offset, EntrySize);
            entry.Clear();
            RecordHelpers.WriteInt48(entry[EntryRelSeqOffset..], relationshipId.Sequence);
            RecordHelpers.WriteInt48(entry[EntryOtherNodeSeqOffset..], otherNodeId.Sequence);
            BinaryPrimitives.WriteInt16LittleEndian(entry[EntryTypeOffset..], checked((short)type.Value));
            entry[EntryFlagsOffset] = flags;
            BinaryPrimitives.WriteInt32LittleEndian(page.Data[PageEntryCountOffset..], entryCount + 1);
        }

        _heads.Set(nodeId, direction, head with { EntryCount = head.EntryCount + 1 });
    }

    public AdjacencyCursor OpenCursor(
        NodeId nodeId,
        Direction direction,
        RelationshipTypeId? typeFilter = null,
        long baseRelHwm = 0)
        => direction switch
        {
            Direction.Outgoing or Direction.Incoming => new DeltaCursor(
                _file, _heads.Get(nodeId, direction), typeFilter, baseRelHwm),
            Direction.Both => new BothDeltaCursor(
                OpenCursor(nodeId, Direction.Outgoing, typeFilter, baseRelHwm),
                OpenCursor(nodeId, Direction.Incoming, typeFilter, baseRelHwm)),
            _ => AdjacencyCursor.Empty,
        };

    public int Count(
        NodeId nodeId,
        Direction direction,
        RelationshipTypeId? typeFilter = null,
        long baseRelHwm = 0,
        int limit = int.MaxValue)
    {
        using var cursor = OpenCursor(nodeId, direction, typeFilter, baseRelHwm);
        int count = 0;
        while (count < limit && cursor.MoveNext())
            count++;
        return count;
    }

    public void Reset()
    {
        _heads.Reset();
        for (long pageNumber = HeaderPageId.Value + 1; pageNumber < _file.PageCount; pageNumber++)
        {
            using var page = _file.PinForWrite(new PageId(pageNumber));
            page.Data.Clear();
        }
    }

    public void ReloadMeta()
    {
        CheckFormatVersion();
        _heads.ReloadMeta();
    }

    private PageId AllocateDeltaPage()
    {
        PageId pageId = _file.AllocatePage(PageKind.RelationshipDeltaRecord);
        using var page = _file.PinForWrite(pageId);
        page.Data.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(page.Data[PageNextOffset..], -1L);
        BinaryPrimitives.WriteInt32LittleEndian(page.Data[PageEntryCountOffset..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(page.Data[PageReservedOffset..], 0);
        return pageId;
    }

    private int ReadEntryCount(PageId pageId)
    {
        using var page = _file.PinForRead(pageId);
        return BinaryPrimitives.ReadInt32LittleEndian(page.Data[PageEntryCountOffset..]);
    }

    private void CheckFormatVersion()
    {
        using var header = _file.PinForRead(HeaderPageId);
        byte actual = header.Data[HeaderFormatOffset];
        if (actual != FormatVersion.Current)
            throw new FormatVersionMismatchException(
                "relationship-delta-pages", actual, FormatVersion.Current);
    }

    private sealed class DeltaCursor : AdjacencyCursor
    {
        private readonly IPagedFile _file;
        private readonly RelationshipTypeId? _typeFilter;
        private readonly long _baseRelHwm;
        private PageId _pageId;
        private int _entryCount;
        private int _entryIndex;
        private bool _pageLoaded;
        private NodeId _neighbor = NodeId.Invalid;
        private RelationshipId _relationship = RelationshipId.Invalid;
        private RelationshipTypeId _type = RelationshipTypeId.Invalid;

        internal DeltaCursor(
            IPagedFile file,
            RelationshipDeltaHead head,
            RelationshipTypeId? typeFilter,
            long baseRelHwm)
        {
            _file = file;
            _typeFilter = typeFilter;
            _baseRelHwm = baseRelHwm;
            _pageId = head.IsEmpty ? PageId.Invalid : head.FirstPageId;
        }

        public override NodeId Neighbor => _neighbor;
        public override RelationshipId Relationship => _relationship;
        public override RelationshipTypeId Type => _type;

        public override bool MoveNext()
        {
            while (_pageId.IsValid)
            {
                if (!_pageLoaded)
                    LoadPageHeader();

                while (_entryIndex < _entryCount)
                {
                    RelationshipDeltaEntry entry = ReadEntry(_pageId, _entryIndex++);
                    if (_baseRelHwm > 0 && entry.RelationshipId.Sequence < _baseRelHwm)
                        continue;
                    if (_typeFilter.HasValue && entry.Type != _typeFilter.Value)
                        continue;

                    _neighbor = entry.OtherNodeId;
                    _relationship = entry.RelationshipId;
                    _type = entry.Type;
                    return true;
                }

                _pageId = ReadNextPage(_pageId);
                _pageLoaded = false;
                _entryIndex = 0;
            }

            return false;
        }

        private void LoadPageHeader()
        {
            if (!_pageId.IsValid || _pageId.Value >= _file.PageCount)
            {
                _pageId = PageId.Invalid;
                _entryCount = 0;
                return;
            }

            using var page = _file.PinForRead(_pageId);
            _entryCount = BinaryPrimitives.ReadInt32LittleEndian(page.Data[PageEntryCountOffset..]);
            _pageLoaded = true;
        }

        private RelationshipDeltaEntry ReadEntry(PageId pageId, int index)
        {
            using var page = _file.PinForRead(pageId);
            int offset = DeltaPageHeaderSize + index * EntrySize;
            ReadOnlySpan<byte> entry = page.Data.Slice(offset, EntrySize);
            return new RelationshipDeltaEntry(
                new RelationshipId(RecordHelpers.ReadInt48(entry[EntryRelSeqOffset..])),
                new NodeId(RecordHelpers.ReadInt48(entry[EntryOtherNodeSeqOffset..])),
                new RelationshipTypeId(BinaryPrimitives.ReadInt16LittleEndian(entry[EntryTypeOffset..])),
                entry[EntryFlagsOffset]);
        }

        private PageId ReadNextPage(PageId pageId)
        {
            using var page = _file.PinForRead(pageId);
            long next = BinaryPrimitives.ReadInt64LittleEndian(page.Data[PageNextOffset..]);
            return next > 0 ? new PageId(next) : PageId.Invalid;
        }
    }

    private sealed class BothDeltaCursor : AdjacencyCursor
    {
        private readonly AdjacencyCursor _outgoing;
        private readonly AdjacencyCursor _incoming;
        private AdjacencyCursor _current;
        private bool _readingIncoming;

        internal BothDeltaCursor(AdjacencyCursor outgoing, AdjacencyCursor incoming)
        {
            _outgoing = outgoing;
            _incoming = incoming;
            _current = outgoing;
        }

        public override NodeId Neighbor => _current.Neighbor;
        public override RelationshipId Relationship => _current.Relationship;
        public override RelationshipTypeId Type => _current.Type;

        public override bool MoveNext()
        {
            if (_current.MoveNext())
                return true;
            if (_readingIncoming)
                return false;

            _readingIncoming = true;
            _current = _incoming;
            return _current.MoveNext();
        }

        public override void Dispose()
        {
            _outgoing.Dispose();
            _incoming.Dispose();
        }
    }
}

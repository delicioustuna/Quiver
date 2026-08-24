using System.Buffers.Binary;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

internal readonly record struct EdgeDeltaEntry(
    EdgeId EdgeId,
    VertexId OtherVertexId,
    EdgeTypeId Type,
    byte Flags);

/// <summary>
/// edge delta entries を vertex/direction ごとの append-only page chain に保持する。
/// </summary>
internal sealed class PersistentEdgeDeltaStore
{
    private const int HeaderFormatOffset = 31;
    private const int PageNextOffset = 0;
    private const int PageEntryCountOffset = 8;
    private const int PageReservedOffset = 12;
    private const int DeltaPageHeaderSize = 16;

    private const int EntrySize = 16;
    private const int EntryEdgeSeqOffset = 0;
    private const int EntryOtherVertexSeqOffset = 6;
    private const int EntryTypeOffset = 12;
    private const int EntryFlagsOffset = 14;

    private static readonly PageId HeaderPageId = new(1);
    internal static int EntriesPerPageForTest => EntriesPerPage;
    private static int EntriesPerPage => (RecordPageMapping.PageBodySize - DeltaPageHeaderSize) / EntrySize;

    private readonly IPagedFile _file;
    private readonly EdgeDeltaHeadStore _heads;

    public PersistentEdgeDeltaStore(IPagedFile file, EdgeDeltaHeadStore heads)
    {
        _file = file;
        _heads = heads;
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

    public void Append(
        VertexId vertexId,
        Direction direction,
        EdgeId edgeId,
        VertexId otherVertexId,
        EdgeTypeId type,
        byte flags = 0)
    {
        if (direction is not (Direction.Outgoing or Direction.Incoming))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (!edgeId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(edgeId));

        EdgeDeltaHead head = _heads.Get(vertexId, direction);
        PageId pageId;
        int entryCount;
        if (head.IsEmpty)
        {
            pageId = AllocateDeltaPage();
            head = new EdgeDeltaHead(pageId, pageId, 0);
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
            RecordHelpers.WriteInt48(entry[EntryEdgeSeqOffset..], edgeId.Sequence);
            RecordHelpers.WriteInt48(entry[EntryOtherVertexSeqOffset..], otherVertexId.Sequence);
            BinaryPrimitives.WriteInt16LittleEndian(entry[EntryTypeOffset..], checked((short)type.Value));
            entry[EntryFlagsOffset] = flags;
            BinaryPrimitives.WriteInt32LittleEndian(page.Data[PageEntryCountOffset..], entryCount + 1);
        }

        _heads.Set(vertexId, direction, head with { EntryCount = head.EntryCount + 1 });
    }

    public AdjacencyCursor OpenCursor(
        VertexId vertexId,
        Direction direction,
        EdgeTypeId? typeFilter = null,
        long baseEdgeHwm = 0)
        => direction switch
        {
            Direction.Outgoing or Direction.Incoming => new DeltaCursor(
                _file, _heads.Get(vertexId, direction), typeFilter, baseEdgeHwm),
            Direction.Both => new BothDeltaCursor(
                OpenCursor(vertexId, Direction.Outgoing, typeFilter, baseEdgeHwm),
                OpenCursor(vertexId, Direction.Incoming, typeFilter, baseEdgeHwm)),
            _ => AdjacencyCursor.Empty,
        };

    public int Count(
        VertexId vertexId,
        Direction direction,
        EdgeTypeId? typeFilter = null,
        long baseEdgeHwm = 0,
        int limit = int.MaxValue)
    {
        using var cursor = OpenCursor(vertexId, direction, typeFilter, baseEdgeHwm);
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
        PageId pageId = _file.AllocatePage(PageKind.EdgeDeltaRecord);
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
        if (actual != StorageFormatVersion.Current)
            throw new StorageFormatMismatchException(
                "edge-delta-pages", actual, StorageFormatVersion.Current);
    }

    private sealed class DeltaCursor : AdjacencyCursor
    {
        private readonly IPagedFile _file;
        private readonly EdgeTypeId? _typeFilter;
        private readonly long _baseEdgeHwm;
        private PageId _pageId;
        private int _entryCount;
        private int _entryIndex;
        private bool _pageLoaded;
        private VertexId _neighbor = VertexId.Invalid;
        private EdgeId _edge = EdgeId.Invalid;
        private EdgeTypeId _type = EdgeTypeId.Invalid;

        internal DeltaCursor(
            IPagedFile file,
            EdgeDeltaHead head,
            EdgeTypeId? typeFilter,
            long baseEdgeHwm)
        {
            _file = file;
            _typeFilter = typeFilter;
            _baseEdgeHwm = baseEdgeHwm;
            _pageId = head.IsEmpty ? PageId.Invalid : head.FirstPageId;
        }

        public override VertexId Neighbor => _neighbor;
        public override EdgeId Edge => _edge;
        public override EdgeTypeId Type => _type;

        public override bool MoveNext()
        {
            while (_pageId.IsValid)
            {
                if (!_pageLoaded)
                    LoadPageHeader();

                while (_entryIndex < _entryCount)
                {
                    EdgeDeltaEntry entry = ReadEntry(_pageId, _entryIndex++);
                    if (_baseEdgeHwm > 0 && entry.EdgeId.Sequence < _baseEdgeHwm)
                        continue;
                    if (_typeFilter.HasValue && entry.Type != _typeFilter.Value)
                        continue;

                    _neighbor = entry.OtherVertexId;
                    _edge = entry.EdgeId;
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

        private EdgeDeltaEntry ReadEntry(PageId pageId, int index)
        {
            using var page = _file.PinForRead(pageId);
            int offset = DeltaPageHeaderSize + index * EntrySize;
            ReadOnlySpan<byte> entry = page.Data.Slice(offset, EntrySize);
            return new EdgeDeltaEntry(
                new EdgeId(RecordHelpers.ReadInt48(entry[EntryEdgeSeqOffset..])),
                new VertexId(RecordHelpers.ReadInt48(entry[EntryOtherVertexSeqOffset..])),
                new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(entry[EntryTypeOffset..])),
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

        public override VertexId Neighbor => _current.Neighbor;
        public override EdgeId Edge => _current.Edge;
        public override EdgeTypeId Type => _current.Type;

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

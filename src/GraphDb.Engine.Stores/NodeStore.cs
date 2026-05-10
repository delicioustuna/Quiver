using System.Buffers.Binary;
using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Stores;

// Record layout (15 bytes):
//  0 Flags(1) | 1 FirstRelId(6) | 7 FirstPropId(6) | 13 LabelId(2)
internal sealed class NodeStore : INodeStore
{
    public const int RecordSize = 15;
    private const byte FlagInUse = 0x01;

    // PageId(0) = PagedFile meta; PageId(1) = NodeStore header; PageId(2+) = records
    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;  // int64
    private const int MetaHwm = 8;       // int64 (next fresh id = high-water mark)
    private const int MetaInUse = 16;    // int64

    public static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 544

    private readonly IPagedFile _file;
    private long _freeHead;
    private long _hwm;
    private long _inUseCount;

    public NodeStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header); // allocates PageId(1)
            _freeHead = -1; _hwm = 0; _inUseCount = 0;
            FlushMeta();
        }
        else
        {
            LoadMeta();
        }
    }

    public long InUseCount => _inUseCount;

    public NodeId Allocate(LabelId labelId)
    {
        long id;
        if (_freeHead >= 0)
        {
            id = _freeHead;
            var (fpid, foff) = Location(id);
            using var fh = _file.PinForRead(fpid);
            // Next-free pointer stored in FirstRelId slot
            _freeHead = RecordHelpers.ReadInt48(fh.Data[(foff + 1)..]);
        }
        else
        {
            id = _hwm++;
        }
        _inUseCount++;

        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        RecordHelpers.WriteInt48(rec[1..], -1L);   // FirstRelId = invalid
        RecordHelpers.WriteInt48(rec[7..], -1L);   // FirstPropId = invalid
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)labelId.Value);
        _file.UnpinDirty(wpid, 0);

        FlushMeta();
        return new NodeId(id);
    }

    public void Free(NodeId nodeId)
    {
        var (pageId, off) = Location(nodeId.Value);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        rec.Clear();
        RecordHelpers.WriteInt48(rec[1..], _freeHead); // chain next-free into FirstRelId slot
        _file.UnpinDirty(pageId, 0);

        _freeHead = nodeId.Value;
        _inUseCount--;
        FlushMeta();
    }

    public NodeReadHandle Read(NodeId nodeId)
    {
        var (pageId, off) = Location(nodeId.Value);
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        bool inUse = (rec[0] & FlagInUse) != 0;
        var firstRel = new RelationshipId(RecordHelpers.ReadInt48(rec[1..]));
        var firstProp = new PropertyId(RecordHelpers.ReadInt48(rec[7..]));
        var label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..]));
        return new NodeReadHandle(nodeId, inUse, firstRel, firstProp, label);
    }

    public NodeWriteHandle Write(NodeId nodeId)
    {
        var (pageId, off) = Location(nodeId.Value);
        // PinForWrite increments pin count. NodeWriteHandle.Dispose() calls UnpinDirty.
        var ph = _file.PinForWrite(pageId);
        return new NodeWriteHandle(_file, pageId, ph.Data.Slice(off, RecordSize));
    }

    public IEnumerable<NodeId> Scan()
    {
        for (long id = 0; id < _hwm; id++)
        {
            var (pageId, off) = Location(id);
            bool inUse;
            {
                using var h = _file.PinForRead(pageId);
                inUse = (h.Data[off] & FlagInUse) != 0;
            }
            if (inUse)
                yield return new NodeId(id);
        }
    }

    // --- internal helpers (used by RelationshipStore to update node's FirstRelId) ---

    internal void UpdateFirstRelId(NodeId nodeId, RelationshipId newFirstRelId)
    {
        var (pageId, off) = Location(nodeId.Value);
        var ph = _file.PinForWrite(pageId);
        RecordHelpers.WriteInt48(ph.Data[(off + 1)..], newFirstRelId.Value);
        _file.UnpinDirty(pageId, 0);
    }

    internal RelationshipId GetFirstRelId(NodeId nodeId)
    {
        var (pageId, off) = Location(nodeId.Value);
        using var h = _file.PinForRead(pageId);
        return new RelationshipId(RecordHelpers.ReadInt48(h.Data[(off + 1)..]));
    }

    // --- internal bulk-load helpers (no per-record FlushMeta) ---

    internal void BulkWrite(long id, int labelId)
    {
        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        RecordHelpers.WriteInt48(rec[1..], -1L);
        RecordHelpers.WriteInt48(rec[7..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)labelId);
        _file.UnpinDirty(wpid, 0);
    }

    internal void BulkUpdateFirstProp(long id, long firstPropId)
    {
        var (pageId, off) = Location(id);
        var ph = _file.PinForWrite(pageId);
        RecordHelpers.WriteInt48(ph.Data[(off + 7)..], firstPropId);
        _file.UnpinDirty(pageId, 0);
    }

    internal void BulkSetHeaders(long hwm, long inUseCount)
    {
        _hwm = hwm;
        _inUseCount = inUseCount;
        _freeHead = -1;
        FlushMeta();
    }

    // --- private ---

    private (PageId pageId, int offset) Location(long id)
    {
        int rpp = RecordsPerPage;
        return (new PageId(id / rpp + 2), (int)(id % rpp) * RecordSize);
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.NodeRecord);
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _freeHead = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaFreeHead..]);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
        _inUseCount = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaInUse..]);
    }

    private void FlushMeta()
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaInUse..], _inUseCount);
        _file.UnpinDirty(HeaderPageId, 0);
    }
}

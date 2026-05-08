using System.Buffers.Binary;
using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Stores;

// Record layout (40 bytes):
//  0 Flags(1) | 1 Source(6) | 7 Target(6) | 13 TypeId(2) |
// 15 SrcPrev(6) | 21 SrcNext(6) | 27 TgtPrev(6) | 33 TgtNext(6) | 39 Pad(1)
internal sealed class RelationshipStore : IRelationshipStore
{
    public const int RecordSize = 40;
    private const byte FlagInUse = 0x01;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;
    private const int MetaHwm = 8;
    private const int MetaInUse = 16;

    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 204

    private readonly IPagedFile _file;
    private long _freeHead;
    private long _hwm;
    private long _inUseCount;

    public RelationshipStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _freeHead = -1; _hwm = 0; _inUseCount = 0;
            FlushMeta();
        }
        else
        {
            LoadMeta();
        }
    }

    public long InUseCount => _inUseCount;

    public RelationshipId Create(INodeStore nodeStore, NodeId source, NodeId target, RelationshipTypeId type)
    {
        long id;
        if (_freeHead >= 0)
        {
            id = _freeHead;
            var (fpid, foff) = Location(id);
            using var fh = _file.PinForRead(fpid);
            _freeHead = RecordHelpers.ReadInt48(fh.Data[(foff + 1)..]);
        }
        else
        {
            id = _hwm++;
        }
        _inUseCount++;
        var relId = new RelationshipId(id);

        // Read current first-rel of source and target
        RelationshipId srcHead = nodeStore is NodeStore ns
            ? ns.GetFirstRelId(source)
            : ReadFirstRelId(nodeStore, source);
        RelationshipId tgtHead = nodeStore is NodeStore ns2
            ? ns2.GetFirstRelId(target)
            : ReadFirstRelId(nodeStore, target);

        // Write new relationship record
        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        RecordHelpers.WriteInt48(rec[1..], source.Value);
        RecordHelpers.WriteInt48(rec[7..], target.Value);
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)type.Value);
        RecordHelpers.WriteInt48(rec[15..], RelationshipId.Invalid.Value);  // SrcPrev
        RecordHelpers.WriteInt48(rec[21..], srcHead.Value);                  // SrcNext
        RecordHelpers.WriteInt48(rec[27..], RelationshipId.Invalid.Value);  // TgtPrev
        RecordHelpers.WriteInt48(rec[33..], tgtHead.Value);                  // TgtNext
        _file.UnpinDirty(wpid, 0);

        // Update old head's SrcPrev / TgtPrev to point back at new rel
        if (srcHead.IsValid)
            UpdateListPrev(srcHead, source, relId);
        if (tgtHead.IsValid && tgtHead != srcHead)
            UpdateListPrev(tgtHead, target, relId);

        // Update both nodes' FirstRelId
        if (nodeStore is NodeStore ns3)
        {
            ns3.UpdateFirstRelId(source, relId);
            ns3.UpdateFirstRelId(target, relId);
        }
        else
        {
            var wSrc = nodeStore.Write(source);
            wSrc.FirstRelationshipId = relId;
            wSrc.Dispose();
            var wTgt = nodeStore.Write(target);
            wTgt.FirstRelationshipId = relId;
            wTgt.Dispose();
        }

        FlushMeta();
        return relId;
    }

    public void Delete(INodeStore nodeStore, RelationshipId relId)
    {
        var rh = Read(relId);
        NodeId src = rh.Source;
        NodeId tgt = rh.Target;
        RelationshipId srcPrev = rh.SourcePrev;
        RelationshipId srcNext = rh.SourceNext;
        RelationshipId tgtPrev = rh.TargetPrev;
        RelationshipId tgtNext = rh.TargetNext;

        // Unlink from source chain
        if (!srcPrev.IsValid)
        {
            // was head of source's list
            if (nodeStore is NodeStore ns)
                ns.UpdateFirstRelId(src, srcNext);
            else
            {
                var w = nodeStore.Write(src);
                w.FirstRelationshipId = srcNext;
                w.Dispose();
            }
        }
        else
        {
            UpdateListNext(srcPrev, src, srcNext);
        }
        if (srcNext.IsValid)
            UpdateListPrev(srcNext, src, srcPrev);

        // Unlink from target chain (only if src != tgt, i.e. not a self-loop)
        if (src != tgt)
        {
            if (!tgtPrev.IsValid)
            {
                if (nodeStore is NodeStore ns)
                    ns.UpdateFirstRelId(tgt, tgtNext);
                else
                {
                    var w = nodeStore.Write(tgt);
                    w.FirstRelationshipId = tgtNext;
                    w.Dispose();
                }
            }
            else
            {
                UpdateListNext(tgtPrev, tgt, tgtNext);
            }
            if (tgtNext.IsValid)
                UpdateListPrev(tgtNext, tgt, tgtPrev);
        }

        // Free the record
        var (pageId, off) = Location(relId.Value);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        rec.Clear();
        RecordHelpers.WriteInt48(rec[1..], _freeHead);
        _file.UnpinDirty(pageId, 0);

        _freeHead = relId.Value;
        _inUseCount--;
        FlushMeta();
    }

    public RelationshipReadHandle Read(RelationshipId relId)
    {
        var (pageId, off) = Location(relId.Value);
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        bool inUse = (rec[0] & FlagInUse) != 0;
        var src = new NodeId(RecordHelpers.ReadInt48(rec[1..]));
        var tgt = new NodeId(RecordHelpers.ReadInt48(rec[7..]));
        var type = new RelationshipTypeId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..]));
        var srcPrev = new RelationshipId(RecordHelpers.ReadInt48(rec[15..]));
        var srcNext = new RelationshipId(RecordHelpers.ReadInt48(rec[21..]));
        var tgtPrev = new RelationshipId(RecordHelpers.ReadInt48(rec[27..]));
        var tgtNext = new RelationshipId(RecordHelpers.ReadInt48(rec[33..]));
        return new RelationshipReadHandle(relId, inUse, src, tgt, type, srcPrev, srcNext, tgtPrev, tgtNext);
    }

    public RelationshipWriteHandle Write(RelationshipId relId)
    {
        var (pageId, off) = Location(relId.Value);
        var ph = _file.PinForWrite(pageId);
        return new RelationshipWriteHandle(_file, pageId, ph.Data.Slice(off, RecordSize));
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore)
    {
        RelationshipId first = nodeStore is NodeStore ns
            ? ns.GetFirstRelId(nodeId)
            : GetFirstRelIdViaInterface(nodeStore, nodeId);
        return new RelationshipEnumerator(this, nodeId, first);
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore,
        RelationshipTypeId type, Direction direction)
    {
        RelationshipId first = nodeStore is NodeStore ns
            ? ns.GetFirstRelId(nodeId)
            : GetFirstRelIdViaInterface(nodeStore, nodeId);
        return new RelationshipEnumerator(this, nodeId, first, type, direction);
    }

    // --- private helpers ---

    private void UpdateListPrev(RelationshipId relId, NodeId side, RelationshipId newPrev)
    {
        var (pageId, off) = Location(relId.Value);
        var ph = _file.PinForWrite(pageId);
        ReadOnlySpan<byte> snap = ph.Data.Slice(off, RecordSize);
        NodeId recSrc = new(RecordHelpers.ReadInt48(snap[1..]));
        int prevOff = recSrc == side ? off + 15 : off + 27;
        RecordHelpers.WriteInt48(ph.Data[prevOff..], newPrev.Value);
        _file.UnpinDirty(pageId, 0);
    }

    private void UpdateListNext(RelationshipId relId, NodeId side, RelationshipId newNext)
    {
        var (pageId, off) = Location(relId.Value);
        var ph = _file.PinForWrite(pageId);
        ReadOnlySpan<byte> snap = ph.Data.Slice(off, RecordSize);
        NodeId recSrc = new(RecordHelpers.ReadInt48(snap[1..]));
        int nextOff = recSrc == side ? off + 21 : off + 33;
        RecordHelpers.WriteInt48(ph.Data[nextOff..], newNext.Value);
        _file.UnpinDirty(pageId, 0);
    }

    private static RelationshipId ReadFirstRelId(INodeStore nodeStore, NodeId nodeId)
    {
        using var h = nodeStore.Read(nodeId);
        return h.FirstRelationshipId;
    }

    private static RelationshipId GetFirstRelIdViaInterface(INodeStore nodeStore, NodeId nodeId)
    {
        using var h = nodeStore.Read(nodeId);
        return h.FirstRelationshipId;
    }

    private (PageId pageId, int offset) Location(long id)
    {
        int rpp = RecordsPerPage;
        return (new PageId(id / rpp + 2), (int)(id % rpp) * RecordSize);
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.RelationshipRecord);
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

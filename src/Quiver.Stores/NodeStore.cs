using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Stores;

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
    // VEC-11: optional sidecar updated on Allocate / Free / BulkSetHeaders so the
    // binary backend's access methods can serve label-filtered scans in O(|L|).
    // null when not wired (e.g. unit tests that construct NodeStore standalone).
    private LabelNodeIndex? _labelIndex;
    private long _freeHead;
    private long _hwm;
    private long _inUseCount;

    public NodeStore(IPagedFile file) : this(file, labelIndex: null) { }

    public NodeStore(IPagedFile file, LabelNodeIndex? labelIndex)
    {
        _file = file;
        _labelIndex = labelIndex;
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

    /// <summary>
    /// VEC-11: 後付けで LabelNodeIndex を接続する。BinaryGraphStorageBackendFactory が
    /// RecoveryManager.Recover() 完了後に index を構築する流れで使う。
    /// </summary>
    public void AttachLabelIndex(LabelNodeIndex labelIndex) => _labelIndex = labelIndex;

    /// <summary>
    /// FT-15: ヘッダページからインメモリのメタ (hwm / freeHead / inUseCount) を読み直す。
    /// abort の before-image 巻き戻しでヘッダページ自体は TX 開始前の状態へ戻っているので、
    /// ここではページから読み直してインメモリのキャッシュを同期するだけでよい。
    /// クラッシュ recovery 後にも呼ばれ、redo / undo で書き換わったヘッダページに追従する。
    /// LabelNodeIndex は abort で OnAllocate/OnFree が宙に浮くため無効化し再構築させる。
    /// </summary>
    internal void ReloadMeta()
    {
        LoadMeta();
        _labelIndex?.Invalidate();
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
        var newId = new NodeId(id);
        _labelIndex?.OnAllocate(newId, labelId);
        return newId;
    }

    public void Free(NodeId nodeId)
    {
        var (pageId, off) = Location(nodeId.Value);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        // VEC-11: capture label before clearing so the sidecar index can
        // remove this NodeId from the right bucket.
        var prevLabel = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..]));
        rec.Clear();
        RecordHelpers.WriteInt48(rec[1..], _freeHead); // chain next-free into FirstRelId slot
        _file.UnpinDirty(pageId, 0);

        _freeHead = nodeId.Value;
        _inUseCount--;
        FlushMeta();
        _labelIndex?.OnFree(nodeId, prevLabel);
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
        // VEC-11: BulkWrite path bypasses Allocate notifications, so invalidate
        // the sidecar. Next lookup will trigger a full rebuild via EnsureBuilt.
        _labelIndex?.Invalidate();
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

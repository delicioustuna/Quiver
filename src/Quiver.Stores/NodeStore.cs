using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Stores;

// FT-26 v2 (MVCC) record layout (31 bytes):
//  0 Flags(1) | 1 FirstRelId(6) | 7 FirstPropId(6) | 13 LabelId(2) | 15 Xmin(8) | 23 Xmax(8)
//
// Xmin / Xmax = TransactionId.Value (long). 0 = unset.
//   Allocate: xmin = MvccContext.CurrentTxId, xmax = 0
//   Free (logical delete): xmax = MvccContext.CurrentTxId
//     チェーン / record 本体は保持 (snapshot reader が辿れるよう)、物理回収は vacuum (OP-3) 担当。
internal sealed class NodeStore : INodeStore
{
    public const int RecordSize = 31;
    private const byte FlagInUse = 0x01;
    internal const int XminOffset = 15;
    internal const int XmaxOffset = 23;

    // PageId(0) = PagedFile meta; PageId(1) = NodeStore header; PageId(2+) = records
    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;       // int64
    private const int MetaHwm = 8;            // int64
    private const int MetaInUse = 16;         // int64
    private const int MetaFormatVersion = 31; // byte (FT-26 sentinel — 詳細は FormatVersion)

    public static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 263

    private readonly IPagedFile _file;
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
            FlushMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
        }
    }

    public void AttachLabelIndex(LabelNodeIndex labelIndex) => _labelIndex = labelIndex;

    internal void ReloadMeta()
    {
        LoadMeta();
        _labelIndex?.Invalidate();
    }

    public long InUseCount => _inUseCount;

    public NodeId Allocate(LabelId labelId)
    {
        // FT-26: 論理削除に伴うチェーン非解除で free list の slot を物理的に再利用しなくなる。
        // フリーリストは vacuum (OP-3) 完了時にのみエントリが入る。それまでは hwm 単調増加。
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

        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        RecordHelpers.WriteInt48(rec[1..], -1L);
        RecordHelpers.WriteInt48(rec[7..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)labelId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(rec[XminOffset..], MvccContext.CurrentTxId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(rec[XmaxOffset..], 0L);
        _file.UnpinDirty(wpid, 0);

        FlushMeta();
        var newId = new NodeId(id);
        _labelIndex?.OnAllocate(newId, labelId);
        return newId;
    }

    public void Free(NodeId nodeId)
    {
        // FT-26 MVCC: 論理削除のみ — xmax をスタンプして record / チェーンは維持する。
        // 物理回収 + free list 投入は vacuum 経路 (OP-3) で行う。
        var (pageId, off) = Location(nodeId.Value);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        var prevLabel = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..]));
        BinaryPrimitives.WriteInt64LittleEndian(rec[XmaxOffset..], MvccContext.CurrentTxId.Value);
        _file.UnpinDirty(pageId, 0);

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
        long xmin = BinaryPrimitives.ReadInt64LittleEndian(rec[XminOffset..]);
        long xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[XmaxOffset..]);
        // FT-26: ambient MVCC コンテキストで可視性をフィルタする。
        // 不可視なら InUse=false に縮退して呼出側に "存在しない" と見せる。
        if (inUse && !Visibility.IsVisibleAmbient(xmin, xmax))
            inUse = false;
        return new NodeReadHandle(nodeId, inUse, firstRel, firstProp, label, xmin, xmax);
    }

    public NodeWriteHandle Write(NodeId nodeId)
    {
        var (pageId, off) = Location(nodeId.Value);
        var ph = _file.PinForWrite(pageId);
        return new NodeWriteHandle(_file, pageId, ph.Data.Slice(off, RecordSize));
    }

    public IEnumerable<NodeId> Scan()
    {
        for (long id = 0; id < _hwm; id++)
        {
            var (pageId, off) = Location(id);
            bool inUse;
            long xmin, xmax;
            {
                using var h = _file.PinForRead(pageId);
                ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
                inUse = (rec[0] & FlagInUse) != 0;
                xmin = BinaryPrimitives.ReadInt64LittleEndian(rec[XminOffset..]);
                xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[XmaxOffset..]);
            }
            if (inUse && Visibility.IsVisibleAmbient(xmin, xmax))
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
        // FT-26: bulk load は MvccContext が無いことが多いため Bootstrap TxId を xmin に。
        // CommittedTxRegistry には常に Bootstrap が登録済みなので全 snapshot で可視。
        BinaryPrimitives.WriteInt64LittleEndian(rec[XminOffset..], TransactionId.Bootstrap.Value);
        BinaryPrimitives.WriteInt64LittleEndian(rec[XmaxOffset..], 0L);
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

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != FormatVersion.V2Mvcc)
            throw new FormatVersionMismatchException("nodes", v, FormatVersion.V2Mvcc);
    }

    private void FlushMeta(bool initialise = false)
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaInUse..], _inUseCount);
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.V2Mvcc;
        _file.UnpinDirty(HeaderPageId, 0);
    }
}

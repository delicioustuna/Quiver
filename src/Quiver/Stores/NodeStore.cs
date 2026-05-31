using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Stores;

/// <summary>OP-3 vacuum: 可視性フィルタを通さない raw ノードレコード。</summary>
internal struct RawNodeRecord
{
    public bool InUse;
    public RelationshipId FirstRelId;
    public PropertyId FirstPropId;
    public LabelId Label;
    public long Xmin;
    public long Xmax;
}

// FT-32 v3 (MVCC sidecar) record layout (15 bytes):
//  0 Flags(1) | 1 FirstRelId(6) | 7 FirstPropId(6) | 13 LabelId(2)
//
// Xmin / Xmax は record から撤去し、EntityVersionMeta sidecar (NodeVersionMeta) に
// localId (= NodeId.Value) をキーとして移管した。
//   Allocate: sidecar.Write(id, { Xmin = MvccContext.CurrentTxId, Xmax = 0 })
//   Free (logical delete): sidecar.UpdateXmax(id, MvccContext.CurrentTxId)
//     チェーン / record 本体は保持 (snapshot reader が辿れるよう)、物理回収は vacuum (OP-3) 担当。
internal sealed class NodeStore : INodeStore
{
    public const int RecordSize = 15;
    private const byte FlagInUse = 0x01;

    // PageId(0) = PagedFile meta; PageId(1) = NodeStore header; PageId(2+) = records
    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;       // int64
    private const int MetaHwm = 8;            // int64
    private const int MetaInUse = 16;         // int64
    private const int MetaFormatVersion = 31; // byte (FT-26 sentinel — 詳細は FormatVersion)

    public static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 544

    private readonly IPagedFile _file;
    private readonly IEntityVersionStore _versions;
    private LabelNodeIndex? _labelIndex;
    private long _freeHead;
    private long _hwm;
    private long _inUseCount;

    public NodeStore(IPagedFile file) : this(file, labelIndex: null, versions: null) { }

    public NodeStore(IPagedFile file, LabelNodeIndex? labelIndex) : this(file, labelIndex, versions: null) { }

    public NodeStore(IPagedFile file, LabelNodeIndex? labelIndex, IEntityVersionStore? versions)
    {
        _file = file;
        _versions = versions ?? new InMemoryEntityVersionStore();
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
        _file.UnpinDirty(wpid, 0);

        // FT-32: xmin/xmax は sidecar に書く。Pstamp=0 / Sstamp=MaxValue は SSN (FT-33) の既定。
        _versions.Write(id, new EntityVersionMeta(MvccContext.CurrentTxId.Value, 0, 0, long.MaxValue));

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
        LabelId prevLabel;
        {
            using var rh = _file.PinForRead(pageId);
            prevLabel = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rh.Data.Slice(off, RecordSize)[13..]));
        }
        // FT-32: 論理削除は sidecar の xmax をスタンプするだけ。record 本体は触らない。
        _versions.UpdateXmax(nodeId.Value, MvccContext.CurrentTxId.Value);

        _inUseCount--;
        FlushMeta();
        _labelIndex?.OnFree(nodeId, prevLabel);
    }

    public NodeReadHandle Read(NodeId nodeId)
    {
        // FT-30: HWM を超える ID / 負の ID は "存在しない" 扱いで safe-return する。
        // これがないと PagedFile.PinForRead が未割当ページの magic=0 を検出して
        // CorruptionException を投げ、NodeExists / HasProperty 等の defensive read API が
        // false を返す契約を破ってしまう (OP-2 sample の GET /nodes/{id} で発覚)。
        if (nodeId.Value < 0 || nodeId.Value >= _hwm)
            return new NodeReadHandle(nodeId, inUse: false, RelationshipId.Invalid, PropertyId.Invalid, default);
        var (pageId, off) = Location(nodeId.Value);
        bool inUse;
        RelationshipId firstRel;
        PropertyId firstProp;
        LabelId label;
        {
            using var h = _file.PinForRead(pageId);
            ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
            inUse = (rec[0] & FlagInUse) != 0;
            firstRel = new RelationshipId(RecordHelpers.ReadInt48(rec[1..]));
            firstProp = new PropertyId(RecordHelpers.ReadInt48(rec[7..]));
            label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..]));
        }
        // FT-32: xmin/xmax は sidecar から引く。物理 free スロット (inUse=false) は
        // sidecar を引かずに早期 return する (無駄な pin を避ける + stale sidecar を読まない)。
        long xmin = 0, xmax = 0;
        if (inUse)
        {
            var meta = _versions.Read(nodeId.Value);
            xmin = meta.Xmin; xmax = meta.Xmax;
            // FT-26: ambient MVCC コンテキストで可視性をフィルタする。
            // 不可視なら InUse=false に縮退して呼出側に "存在しない" と見せる。
            if (!Visibility.IsVisibleAmbient(xmin, xmax))
                inUse = false;
        }
        // FT-33: 可視バージョンを観測したら SSN read-set に記録する (Serializable 時のみ。
        // 直接 Read だけでなく traversal の隣接走査もこの経路を通る)。
        if (inUse) MvccContext.RecordRead(EntityKind.Node, nodeId.Value);
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
            {
                using var h = _file.PinForRead(pageId);
                ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
                inUse = (rec[0] & FlagInUse) != 0;
            }
            if (!inUse) continue;
            var meta = _versions.Read(id);
            if (Visibility.IsVisibleAmbient(meta.Xmin, meta.Xmax))
            {
                // FT-33: scan で観測した可視ノードも SSN read-set に記録する。
                MvccContext.RecordRead(EntityKind.Node, id);
                yield return new NodeId(id);
            }
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
        // FT-26/FT-32: bulk load は MvccContext が無いことが多いため Bootstrap TxId を xmin に。
        // CommittedTxRegistry には常に Bootstrap が登録済みなので全 snapshot で可視。
        _versions.Write(id, new EntityVersionMeta(TransactionId.Bootstrap.Value, 0, 0, long.MaxValue));
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

    /// <summary>
    /// OP-3 vacuum: <paramref name="horizonTxId"/> 未満で xmax がコミット済みな dead version を
    /// 物理回収し、slot を free list に投入する。inUseCount は <see cref="Free"/> 時に既に
    /// 減算されているので触らない。<paramref name="committed"/> は xmax のコミット判定に使う
    /// registry。末尾の連続 free slot が hwm を下げられる場合は hwm も縮める。
    /// </summary>
    /// <remarks>
    /// 呼び出し側 (<c>Vacuum.Run</c>) はアクティブトランザクションが 0 の前提を保証する。
    /// ラベル索引は <see cref="Free"/> 時点で既に <c>OnFree</c> 済みなので触らない。
    /// </remarks>
    /// <returns>物理回収した dead version 数。</returns>
    internal int VacuumDeadVersions(long horizonTxId, CommittedTxRegistry committed)
    {
        int reclaimed = 0;
        for (long id = 0; id < _hwm; id++)
        {
            var (pageId, off) = Location(id);
            bool reclaimThis;
            {
                using var h = _file.PinForRead(pageId);
                ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
                bool inUse = (rec[0] & FlagInUse) != 0;
                if (!inUse) { continue; } // 既に物理 free
                long xmax = _versions.Read(id).Xmax; // FT-32: xmax は sidecar から
                reclaimThis = xmax != 0
                    && xmax < horizonTxId
                    && committed.IsCommitted(xmax);
            }
            if (!reclaimThis) continue;

            var ph = _file.PinForWrite(pageId);
            Span<byte> rec2 = ph.Data.Slice(off, RecordSize);
            rec2.Clear();
            // free list link: byte[1..7] に prev head (48-bit signed)。
            RecordHelpers.WriteInt48(rec2[1..], _freeHead);
            _file.UnpinDirty(pageId, 0);
            _freeHead = id;
            reclaimed++;
        }

        if (reclaimed > 0)
        {
            ShrinkHwmFromTrailingFreeSlots();
            FlushMeta();
        }
        return reclaimed;
    }

    private void ShrinkHwmFromTrailingFreeSlots()
    {
        long oldHwm = _hwm;
        long newHwm = oldHwm;
        while (newHwm > 0)
        {
            long candidate = newHwm - 1;
            var (pageId, off) = Location(candidate);
            using var h = _file.PinForRead(pageId);
            ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
            bool inUse = (rec[0] & FlagInUse) != 0;
            if (inUse) break;
            newHwm--;
        }
        if (newHwm == oldHwm) return;

        // 末尾 slot を hwm 範囲外に出すので、free list 内の対象 slot を取り除き、繋ぎ直す。
        long head = _freeHead;
        var keep = new List<long>();
        while (head >= 0)
        {
            if (head < newHwm) keep.Add(head);
            var (pageId, off) = Location(head);
            using var h = _file.PinForRead(pageId);
            long next = RecordHelpers.ReadInt48(h.Data[(off + 1)..]);
            head = next;
        }
        long newHead = -1;
        for (int i = keep.Count - 1; i >= 0; i--)
        {
            long id = keep[i];
            var (pageId, off) = Location(id);
            var ph = _file.PinForWrite(pageId);
            Span<byte> rec = ph.Data.Slice(off, RecordSize);
            RecordHelpers.WriteInt48(rec[1..], newHead);
            _file.UnpinDirty(pageId, 0);
            newHead = id;
        }
        _freeHead = newHead;
        _hwm = newHwm;
    }

    /// <summary>OP-3 / テスト用。現在の HWM スロット数 (free 含む)。</summary>
    internal long Hwm => _hwm;

    /// <summary>
    /// OP-5: 現在の <c>_hwm</c> を保持するのに必要な最小ページ数 (meta=0 + header=1 + record pages)。
    /// <c>_hwm=0</c> でも meta/header の 2 ページは残す。
    /// </summary>
    internal long ComputeRequiredPageCount()
        => _hwm == 0 ? 2L : ((_hwm - 1) / RecordsPerPage) + 3L;

    /// <summary>OP-5: 内部 PagedFile への参照 (vacuum/truncate 経路で使用)。</summary>
    internal IPagedFile UnderlyingFile => _file;

    /// <summary>OP-3 / テスト用。free list 先頭 (-1 で空)。</summary>
    internal long FreeHead => _freeHead;

    /// <summary>
    /// OP-3 vacuum: 可視性フィルタを通さない raw 読み取り。<paramref name="id"/> 範囲外は
    /// InUse=false の値を返す。
    /// </summary>
    internal RawNodeRecord ReadRaw(long id)
    {
        if (id < 0 || id >= _hwm) return default;
        var (pageId, off) = Location(id);
        var meta = _versions.Read(id); // FT-32: xmin/xmax は sidecar から
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        return new RawNodeRecord
        {
            InUse = (rec[0] & FlagInUse) != 0,
            FirstRelId = new RelationshipId(RecordHelpers.ReadInt48(rec[1..])),
            FirstPropId = new PropertyId(RecordHelpers.ReadInt48(rec[7..])),
            Label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..])),
            Xmin = meta.Xmin,
            Xmax = meta.Xmax,
        };
    }

    /// <summary>OP-3 vacuum: ノードの FirstPropId を書き換える。chain 整理用。</summary>
    internal void UpdateFirstPropId(NodeId nodeId, PropertyId newFirstPropId)
    {
        var (pageId, off) = Location(nodeId.Value);
        var ph = _file.PinForWrite(pageId);
        RecordHelpers.WriteInt48(ph.Data[(off + 7)..], newFirstPropId.Value);
        _file.UnpinDirty(pageId, 0);
    }

    /// <summary>OP-3 vacuum: ノードの FirstRelId raw 取得。<see cref="GetFirstRelId"/> の internal エイリアス。</summary>
    internal RelationshipId GetFirstRelIdRaw(NodeId nodeId) => GetFirstRelId(nodeId);

    /// <summary>OP-3 vacuum: ノードの FirstRelId 書き換え。<see cref="UpdateFirstRelId"/> の internal エイリアス。</summary>
    internal void UpdateFirstRelIdRaw(NodeId nodeId, RelationshipId newFirstRelId)
        => UpdateFirstRelId(nodeId, newFirstRelId);

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
        if (v != FormatVersion.V3MvccSidecar)
            throw new FormatVersionMismatchException("nodes", v, FormatVersion.V3MvccSidecar);
    }

    private void FlushMeta(bool initialise = false)
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaInUse..], _inUseCount);
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.V3MvccSidecar;
        _file.UnpinDirty(HeaderPageId, 0);
    }
}

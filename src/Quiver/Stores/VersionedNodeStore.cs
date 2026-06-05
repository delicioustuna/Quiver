using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// ARCH-5c Phase 2: <see cref="VersionedRecordHeap"/> + <see cref="ItemPointerMap"/> 上に実装した
/// <see cref="INodeStore"/>。MVCC の xmin/xmax を record (version ヘッダ) へ再内包し、論理 ID
/// (Sequence) は map 経由で物理位置へ解決する統一レコードモデル (docs/design/11 §6.3)。
///
/// <para>ノード payload (19B, version ヘッダ 24B の後ろ):</para>
/// <code>
///   [0]  flags       : u8       (FlagInUse)
///   [1]  firstRel    : Int48    (Sequence)
///   [7]  firstProp   : Int48    (Sequence)
///   [13] label       : i16
///   [15] generation  : u32      (ARCH-5b incarnation)
/// </code>
/// 先頭 15B (<see cref="NodeFieldsSize"/>) は旧 NodeStore の固定レイアウトと同形のため、
/// <see cref="NodeWriteHandle"/> をそのまま再利用して in-place 更新できる。
///
/// <para>ID モデル: Sequence は monotonic (slot 非再利用)。slot 再利用後の stale 参照は
/// 版チェーン + map-null + MVCC visibility で弾くため free list / 世代不一致は不要だが、
/// ARCH-5b の Kind+Gen+Seq ID 契約 (§3) を保つため generation を record に保持する
/// (monotonic では seq 毎に 1 固定)。</para>
/// </summary>
internal sealed class VersionedNodeStore : INodeStore
{
    private const int PayloadSize = 19;
    private const int OffFlags = 0;
    private const int OffFirstRel = 1;
    private const int OffFirstProp = 7;
    private const int OffLabel = 13;
    private const int OffGeneration = 15;
    private const byte FlagInUse = 0x01;

    /// <summary>NodeWriteHandle が触る先頭領域 (flags + firstRel + firstProp + label)。</summary>
    private const int NodeFieldsSize = 15;

    private static readonly int HdrSize = VersionedRecordHeap.VersionHeaderSize;

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private readonly VersionedRecordHeap _heap;
    private LabelNodeIndex? _labelIndex;
    private long _inUseCount;

    public VersionedNodeStore(IPagedFile heapFile, ItemPointerMap map, LabelNodeIndex? labelIndex = null)
    {
        _file = heapFile;
        _map = map;
        _heap = new VersionedRecordHeap(heapFile, map);
        _labelIndex = labelIndex;
        _inUseCount = RecomputeInUse();
    }

    public void AttachLabelIndex(LabelNodeIndex labelIndex) => _labelIndex = labelIndex;

    public long InUseCount => _inUseCount;

    public NodeId Allocate(LabelId labelId)
    {
        long seq = _map.Hwm;       // monotonic Sequence
        const uint generation = 1; // slot 非再利用 → 世代は 1 固定 (ID 契約のため保持)

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffFirstRel..], -1L);
        RecordHelpers.WriteInt48(payload[OffFirstProp..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffLabel..], (short)labelId.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[OffGeneration..], generation);

        _heap.Insert(seq, payload, MvccContext.CurrentTxId.Value);
        _inUseCount++;

        var newId = NodeId.Create(seq, (int)generation);
        _labelIndex?.OnAllocate(newId, labelId);
        return newId;
    }

    public void Free(NodeId nodeId)
    {
        long seq = nodeId.Sequence;
        if (!_heap.TryReadHeadRaw(seq, out var payload, out _, out long xmax)) return;
        if (xmax != 0) return; // 既に論理削除済
        var prevLabel = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(OffLabel)));
        _heap.StampXmax(seq, MvccContext.CurrentTxId.Value);
        _inUseCount--;
        _labelIndex?.OnFree(nodeId, prevLabel);
    }

    public NodeReadHandle Read(NodeId nodeId)
    {
        long seq = nodeId.Sequence;
        if (seq < 0 || seq >= _map.Hwm)
            return new NodeReadHandle(nodeId, inUse: false, RelationshipId.Invalid, PropertyId.Invalid, default);

        if (!_heap.TryReadVisible(seq, AmbientVisible, out var payload, out long xmin, out long xmax))
            return new NodeReadHandle(nodeId, inUse: false, RelationshipId.Invalid, PropertyId.Invalid, default);

        var span = payload.AsSpan();
        var firstRel = new RelationshipId(RecordHelpers.ReadInt48(span[OffFirstRel..]));
        var firstProp = new PropertyId(RecordHelpers.ReadInt48(span[OffFirstProp..]));
        var label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(span[OffLabel..]));
        int gen = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[OffGeneration..]);

        bool inUse = true;
        // ARCH-5b: 世代付き NodeId (Allocate 由来) は現世代と照合。gen=0 (内部パイプライン) はスキップ。
        int carriedGen = nodeId.Generation;
        if (carriedGen != 0 && carriedGen != gen) inUse = false;

        if (inUse) MvccContext.RecordRead(EntityKind.Node, seq);
        return new NodeReadHandle(nodeId, inUse, firstRel, firstProp, label, xmin, xmax);
    }

    public NodeWriteHandle Write(NodeId nodeId)
    {
        var ptr = _heap.GetHead(nodeId.Sequence);
        if (ptr.IsNull)
            throw new CorruptionException($"Write on missing node seq={nodeId.Sequence}");
        var pageId = new PageId(ptr.PageId);
        var ph = _file.PinForWrite(pageId);
        var sp = new SlottedPage(ph.Data);
        if (!sp.TryGetMutable(ptr.Slot, out var rec))
        {
            _file.Unpin(pageId);
            throw new CorruptionException($"missing version slot for node seq={nodeId.Sequence}");
        }
        // 先頭 15B (flags+rel+prop+label) を NodeWriteHandle に渡す。Dispose で UnpinDirty。
        var fields = rec.Slice(HdrSize, NodeFieldsSize);
        return new NodeWriteHandle(_file, pageId, fields);
    }

    public IEnumerable<NodeId> Scan()
    {
        long hwm = _map.Hwm;
        for (long seq = 0; seq < hwm; seq++)
        {
            if (_heap.TryReadVisible(seq, AmbientVisible, out _, out _, out _))
            {
                MvccContext.RecordRead(EntityKind.Node, seq);
                // ARCH-5b: パイプラインは Sequence 空間 (gen=0)。世代は利用者境界で stamp。
                yield return new NodeId(seq);
            }
        }
    }

    public int CurrentGeneration(long localId)
    {
        if (localId < 0 || localId >= _map.Hwm) return -1;
        if (!_heap.TryReadHeadRaw(localId, out var payload, out _, out _)) return -1;
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(OffGeneration));
    }

    // --- internal helpers (RelationshipStore / vacuum 経路用、旧 NodeStore と同シグネチャ) ---

    internal void UpdateFirstRelId(NodeId nodeId, RelationshipId newFirstRelId)
        => MutateHeadField(nodeId.Sequence, OffFirstRel, newFirstRelId.Sequence);

    internal void UpdateFirstPropId(NodeId nodeId, PropertyId newFirstPropId)
        => MutateHeadField(nodeId.Sequence, OffFirstProp, newFirstPropId.Sequence);

    internal RelationshipId GetFirstRelId(NodeId nodeId)
    {
        var ptr = _heap.GetHead(nodeId.Sequence);
        if (ptr.IsNull) return RelationshipId.Invalid;
        using var h = _file.PinForRead(new PageId(ptr.PageId));
        var sp = new ReadOnlySlottedPage(h.Data);
        if (!sp.TryGet(ptr.Slot, out var rec)) return RelationshipId.Invalid;
        return new RelationshipId(RecordHelpers.ReadInt48(rec[(HdrSize + OffFirstRel)..]));
    }

    internal RelationshipId GetFirstRelIdRaw(NodeId nodeId) => GetFirstRelId(nodeId);

    internal void UpdateFirstRelIdRaw(NodeId nodeId, RelationshipId newFirstRelId)
        => UpdateFirstRelId(nodeId, newFirstRelId);

    /// <summary>OP-3 vacuum: 可視性フィルタ無しの head version raw 読み取り。範囲外 / 未登録は default。</summary>
    internal RawNodeRecord ReadRaw(long id)
    {
        if (id < 0 || id >= _map.Hwm) return default;
        if (!_heap.TryReadHeadRaw(id, out var payload, out long xmin, out long xmax)) return default;
        var s = payload.AsSpan();
        return new RawNodeRecord
        {
            InUse = (s[OffFlags] & FlagInUse) != 0,
            FirstRelId = new RelationshipId(RecordHelpers.ReadInt48(s[OffFirstRel..])),
            FirstPropId = new PropertyId(RecordHelpers.ReadInt48(s[OffFirstProp..])),
            Label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(s[OffLabel..])),
            Xmin = xmin,
            Xmax = xmax,
        };
    }

    /// <summary>OP-3 / テスト用。採番済み Sequence 数 (= 最大 seq + 1)。</summary>
    internal long Hwm => _map.Hwm;

    /// <summary>FT-15 / recovery 用: map メタを読み直し inUse を再計算する。</summary>
    internal void ReloadMeta()
    {
        _map.ReloadMeta();
        _inUseCount = RecomputeInUse();
        _labelIndex?.Invalidate();
    }

    // --- private ---

    private static bool AmbientVisible(long xmin, long xmax) => Visibility.IsVisibleAmbient(xmin, xmax);

    private void MutateHeadField(long seq, int payloadOffset, long sequenceValue)
    {
        var ptr = _heap.GetHead(seq);
        if (ptr.IsNull) return;
        var pageId = new PageId(ptr.PageId);
        using var ph = _file.PinForWrite(pageId);
        var sp = new SlottedPage(ph.Data);
        if (sp.TryGetMutable(ptr.Slot, out var rec))
            RecordHelpers.WriteInt48(rec[(HdrSize + payloadOffset)..], sequenceValue);
    }

    private long RecomputeInUse()
    {
        long count = 0;
        long hwm = _map.Hwm;
        for (long seq = 0; seq < hwm; seq++)
            if (_heap.TryReadHeadRaw(seq, out _, out _, out long xmax) && xmax == 0)
                count++;
        return count;
    }
}

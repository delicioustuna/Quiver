using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// ARCH-5c Phase 2: <see cref="VersionedRecordHeap"/> + <see cref="ItemPointerMap"/> 上に実装した
/// ノードストア。旧 <c>NodeStore</c> の置き換えで、論理 ID (Sequence) を map 経由で物理位置へ
/// 解決する。固定サイズ record 配列をやめ可変長 slotted record にすることで、Phase 3 の
/// property inline 化の土台になる。
///
/// <para>ノード payload (15B, 旧 NodeStore record と同形 — version ヘッダ 24B の後ろ):</para>
/// <code>
///   [0]  flags     : u8     (FlagInUse)
///   [1]  firstRel  : Int48  (Sequence)
///   [7]  firstProp : Int48  (Sequence)
///   [13] label     : i16
/// </code>
/// 先頭 15B がそのまま <see cref="NodeWriteHandle"/> のレイアウトと一致するので in-place 更新に再利用する。
///
/// <para><b>Phase 2 staging</b>: MVCC (xmin/xmax) / Generation / SSN (Pstamp/Sstamp) /
/// commit-stamp 高水位は従来どおり <see cref="IEntityVersionStore"/> sidecar で管理する
/// (SSN の依存を変えないため)。xmin/xmax の record 再内包と sidecar 廃止は版チェーンが要る
/// Phase 3 へ後ろ倒し。Sequence は vacuum 回収後に再利用する (ItemPointerMap の free list)。
/// slot 再利用に伴う stale 参照は ARCH-3/5b の世代カウンタ照合 + MVCC visibility で弾く
/// (旧 NodeStore と同セマンティクス)。</para>
/// </summary>
internal sealed class VersionedNodeStore : INodeStore
{
    private const int PayloadSize = 15;
    private const int OffFlags = 0;
    private const int OffFirstRel = 1;
    private const int OffFirstProp = 7;
    private const int OffLabel = 13;
    private const byte FlagInUse = 0x01;

    private static readonly int HdrSize = VersionedRecordHeap.VersionHeaderSize;

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private readonly VersionedRecordHeap _heap;
    private readonly IEntityVersionStore _versions;
    private LabelNodeIndex? _labelIndex;
    private long _inUseCount;

    public VersionedNodeStore(IPagedFile heapFile, ItemPointerMap map,
        LabelNodeIndex? labelIndex = null, IEntityVersionStore? versions = null)
    {
        _file = heapFile;
        _map = map;
        _heap = new VersionedRecordHeap(heapFile, map);
        _versions = versions ?? new InMemoryEntityVersionStore();
        _labelIndex = labelIndex;
        _inUseCount = RecomputeInUse();
    }

    public void AttachLabelIndex(LabelNodeIndex labelIndex) => _labelIndex = labelIndex;

    public long InUseCount => _inUseCount;

    public NodeId Allocate(LabelId labelId)
    {
        // ARCH-3/5b: vacuum 回収済み seq を free list から再利用する。世代が上限に達した seq は
        // 永久退役 (ABA 回避)。空なら hwm から新規採番。
        long seq = -1;
        while (true)
        {
            long cand = _map.PopFreeSeq();
            if (cand < 0) break;
            if (_versions.Read(cand).Generation >= EntityRef.MaxGeneration) continue;
            seq = cand;
            break;
        }
        if (seq < 0) seq = _map.Hwm;
        // 世代は sidecar 由来。新規 seq は Unset(0)→1、再利用 seq は前回値 +1。
        long generation = _versions.Read(seq).Generation + 1;

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffFirstRel..], -1L);
        RecordHelpers.WriteInt48(payload[OffFirstProp..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffLabel..], (short)labelId.Value);

        _heap.Insert(seq, payload, MvccContext.CurrentTxId.Value);
        _versions.Write(seq, new EntityVersionMeta(MvccContext.CurrentTxId.Value, 0, 0, long.MaxValue, generation));
        _inUseCount++;

        var newId = NodeId.Create(seq, (int)generation);
        _labelIndex?.OnAllocate(newId, labelId);
        return newId;
    }

    public void Free(NodeId nodeId)
    {
        long seq = nodeId.Sequence;
        // Phase 3a: xmin/xmax は heap version へ再内包。論理削除は head version に xmax をスタンプ。
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

        // Phase 3a: 可視性は heap version の xmin/xmax で判定する (TryReadVisible が版チェーンを辿る)。
        if (!_heap.TryReadVisible(seq, AmbientVisible, out var payload, out long xmin, out long xmax))
            return new NodeReadHandle(nodeId, inUse: false, RelationshipId.Invalid, PropertyId.Invalid, default);

        var span = payload.AsSpan();
        var firstRel = new RelationshipId(RecordHelpers.ReadInt48(span[OffFirstRel..]));
        var firstProp = new PropertyId(RecordHelpers.ReadInt48(span[OffFirstProp..]));
        var label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(span[OffLabel..]));

        bool inUse = true;
        // ARCH-5b: 世代付き NodeId は現世代 (sidecar) と照合。gen=0 (内部パイプライン) はスキップ。
        int carriedGen = nodeId.Generation;
        if (carriedGen != 0 && carriedGen != (int)_versions.Read(seq).Generation)
            inUse = false;

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
        var fields = rec.Slice(HdrSize, PayloadSize);
        return new NodeWriteHandle(_file, pageId, fields);
    }

    public IEnumerable<NodeId> Scan()
    {
        long hwm = _map.Hwm;
        for (long seq = 0; seq < hwm; seq++)
        {
            // Phase 3a: 可視性は heap version で判定 (null head / 不可視は false)。
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
        long gen = _versions.Read(localId).Generation;
        return gen > int.MaxValue ? int.MaxValue : (int)gen;
    }

    // --- internal helpers (RelationshipStore fast-path / vacuum / bulk 経路用) ---

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
        // Phase 3a: xmin/xmax は heap version から (sidecar ではなく)。
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

    internal void UpdateFirstPropIdRaw(NodeId nodeId, PropertyId newFirstPropId)
        => UpdateFirstPropId(nodeId, newFirstPropId);

    /// <summary>採番済み Sequence 数 (= 最大 seq + 1)。</summary>
    internal long Hwm => _map.Hwm;

    /// <summary>OP-3 / テスト用。free list は持たない (monotonic seq)。</summary>
    internal long FreeHead => -1;

    /// <summary>
    /// OP-5: heap モデルは物理 truncate での縮小をしない (slot が散在するため)。現ページ数を返し、
    /// vacuum の truncate を no-op にする。
    /// </summary>
    internal long ComputeRequiredPageCount() => _file.PageCount;

    /// <summary>OP-5: 内部 heap PagedFile。</summary>
    internal IPagedFile UnderlyingFile => _file;

    /// <summary>
    /// OP-3 vacuum: horizon 未満で xmax がコミット済みの dead ノードを heap から物理回収する
    /// (全 version slot を tombstone + map エントリ null 化)。inUseCount は <see cref="Free"/> で
    /// 既に減算済みなので触らない。
    /// </summary>
    internal int VacuumDeadVersions(long horizonTxId, CommittedTxRegistry committed)
    {
        int reclaimed = 0;
        long hwm = _map.Hwm;
        for (long seq = 0; seq < hwm; seq++)
        {
            if (!_heap.TryReadHeadRaw(seq, out _, out _, out long xmax)) continue;
            if (xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax))
            {
                _heap.Remove(seq);   // 全 version slot tombstone + map entry null
                _map.PushFreeSeq(seq); // seq を再利用待ちへ (世代は sidecar に残る)
                reclaimed++;
            }
        }
        return reclaimed;
    }

    // --- bulk-load helpers (no per-record sidecar flush nuance; heap insert handles paging) ---

    internal void BulkWrite(long id, int labelId)
    {
        Span<byte> payload = stackalloc byte[PayloadSize];
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffFirstRel..], -1L);
        RecordHelpers.WriteInt48(payload[OffFirstProp..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffLabel..], (short)labelId);
        _heap.Insert(id, payload, TransactionId.Bootstrap.Value);
        // FT-26/FT-32: bulk load は tx 外。Bootstrap を xmin に、Generation=1 (新規 slot)。
        _versions.Write(id, new EntityVersionMeta(TransactionId.Bootstrap.Value, 0, 0, long.MaxValue, 1));
    }

    internal void BulkUpdateFirstProp(long id, long firstPropId)
        => MutateHeadField(id, OffFirstProp, firstPropId);

    internal void BulkSetHeaders(long hwm, long inUseCount)
    {
        // hwm は map.Hwm が BulkWrite 経由で既に到達済み。inUse のみ採用。
        _inUseCount = inUseCount;
        _labelIndex?.Invalidate();
    }

    /// <summary>FT-15 / recovery 用: map メタを読み直し inUse を再計算する。</summary>
    internal void ReloadMeta()
    {
        _map.ReloadMeta();
        _heap.ReloadMeta();
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
            // Phase 3a: live = heap head が存在し xmax==0 (sidecar ではなく heap version 由来)。
            if (_heap.TryReadHeadRaw(seq, out _, out _, out long xmax) && xmax == 0)
                count++;
        return count;
    }
}

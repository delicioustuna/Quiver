using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>可視性フィルターを通さない vertex head の物理状態。</summary>
internal struct RawVertexRecord
{
    public bool InUse;
    public EdgeId FirstEdgeId;
    public PropertyVersionRef FirstPropertyRef;
    public LabelId Label;
    public long Xmin;
    public long Xmax;
}

/// <summary>
/// <see cref="VersionedRecordHeap"/> + <see cref="ItemPointerMap"/> 上に実装した
/// Vertexストア。旧 <c>VertexStore</c> の置き換えで、論理 ID (Sequence) を map 経由で物理位置へ
/// 解決する。固定サイズ record 配列をやめ、versioned slotted record に格納する。
///
/// <para>Vertex payload (15B, 旧 VertexStore record と同形 — version ヘッダ 24B の後ろ):</para>
/// <code>
///   [0]  flags     : u8     (FlagInUse)
///   [1]  firstEdge  : Int48  (Sequence)
///   [7]  firstProp : Int48  (Sequence)
///   [13] label     : i16
/// </code>
/// 先頭 15B がそのまま <see cref="VertexWriteHandle"/> のレイアウトと一致するので in-place 更新に再利用する。
///
/// <para>Generation と SSN 用 stamp は <see cref="IEntityVersionStore"/> sidecar で管理する。
/// xmin/xmax は versioned record header に保持する。
/// Sequence は vacuum 回収後に再利用する (ItemPointerMap の free list)。
/// slot 再利用に伴う stale 参照は世代カウンタ照合 + MVCC visibility で弾く
/// (旧 VertexStore と同セマンティクス)。</para>
/// </summary>
internal sealed class VersionedVertexStore : IVertexStore
{
    // 固定フィールド領域 (VertexWriteHandle が in-place 更新する先頭 15B)。
    private const int PayloadSize = 15;
    private const int OffFlags = 0;
    private const int OffFirstEdge = 1;
    private const int OffFirstProp = 7;
    private const int OffLabel = 13;
    private const byte FlagInUse = 0x01;
    private static readonly int HdrSize = VersionedRecordHeap.VersionHeaderSize;

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private readonly VersionedRecordHeap _heap;
    private readonly IEntityVersionStore _versions;
    private LabelVertexIndex? _labelIndex;
    private long _inUseCount;
    // gen-stamp-fastpath: 世代再利用が一度でも起きたか。false の間は全ライブ slot の
    // generation = 1 が成立し、CurrentGeneration を sidecar read 無しで確定できる。
    private bool _anyReuse;

    public VersionedVertexStore(IPagedFile heapFile, ItemPointerMap map,
        LabelVertexIndex? labelIndex = null, IEntityVersionStore? versions = null)
    {
        _file = heapFile;
        _map = map;
        _heap = new VersionedRecordHeap(heapFile, map);
        _versions = versions ?? new InMemoryEntityVersionStore();
        _labelIndex = labelIndex;
        _inUseCount = RecomputeInUse();
        _anyReuse = _versions.AnyGenerationReuse;
    }

    public void AttachLabelIndex(LabelVertexIndex labelIndex) => _labelIndex = labelIndex;

    public long InUseCount => _inUseCount;

    public VertexId Allocate(LabelId labelId)
    {
        // vacuum 回収済み seq を free list から再利用する。世代が上限に達した seq は
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
        // gen-stamp-fastpath: generation >= 2 は free list の slot 再利用。以後 stamping は
        // 高速パス (gen=1 即返し) を使えないため、永続フラグを立て CurrentGeneration を実読みへ戻す。
        if (generation >= 2 && !_anyReuse)
        {
            _versions.MarkGenerationReuse();
            _anyReuse = true;
        }

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffFirstEdge..], -1L);
        RecordHelpers.WriteInt48(payload[OffFirstProp..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffLabel..], (short)labelId.Value);

        _heap.Insert(seq, payload, MvccContext.CurrentTxId.Value);
        _versions.Write(seq, new EntityVersionMeta(MvccContext.CurrentTxId.Value, 0, 0, long.MaxValue, generation));
        _inUseCount++;

        var newId = VertexId.Create(seq, (int)generation);
        _labelIndex?.OnAllocate(newId, labelId);
        return newId;
    }

    public void Free(VertexId vertexId)
    {
        long seq = vertexId.Sequence;
        // 論理削除は head version に xmax をスタンプする。
        if (!_heap.TryReadHeadRaw(seq, out var payload, out _, out long xmax)) return;
        if (xmax != 0) return; // 既に論理削除済
        var prevLabel = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(OffLabel)));
        _heap.StampXmax(seq, MvccContext.CurrentTxId.Value);
        _inUseCount--;
        _labelIndex?.OnFree(vertexId, prevLabel);
    }

    public VertexReadHandle Read(VertexId vertexId)
    {
        long seq = vertexId.Sequence;
        if (seq < 0 || seq >= _map.Hwm)
            return new VertexReadHandle(vertexId, inUse: false, EdgeId.Invalid, PropertyVersionRef.Invalid, default);

        // 可視性は heap version の xmin/xmax で判定する (TryReadVisible が版チェーンを辿る)。
        if (!_heap.TryReadVisible(seq, AmbientVisible, out var payload, out long xmin, out long xmax))
            return new VertexReadHandle(vertexId, inUse: false, EdgeId.Invalid, PropertyVersionRef.Invalid, default);

        var span = payload.AsSpan();
        var firstEdge = new EdgeId(RecordHelpers.ReadInt48(span[OffFirstEdge..]));
        var firstProp = new PropertyVersionRef(RecordHelpers.ReadInt48(span[OffFirstProp..]));
        var label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(span[OffLabel..]));

        bool inUse = true;
        // 世代付き VertexId は現世代 (sidecar) と照合。gen=0 (内部パイプライン) はスキップ。
        int carriedGen = vertexId.Generation;
        if (carriedGen != 0 && carriedGen != (int)_versions.Read(seq).Generation)
            inUse = false;

        if (inUse) MvccContext.RecordRead(EntityKind.Vertex, seq);
        var resolvedId = VertexId.Create(seq, CurrentGeneration(seq));
        return new VertexReadHandle(resolvedId, inUse, firstEdge, firstProp, label, xmin, xmax);
    }

    public VertexWriteHandle Write(VertexId vertexId)
    {
        var ptr = _heap.GetHead(vertexId.Sequence);
        if (ptr.IsNull)
            throw new CorruptionException($"Write on missing vertex seq={vertexId.Sequence}");
        var pageId = new PageId(ptr.PageId);
        var ph = _file.PinForWrite(pageId);
        var sp = new SlottedPage(ph.Data);
        if (!sp.TryGetMutable(ptr.Slot, out var rec))
        {
            _file.Unpin(pageId);
            throw new CorruptionException($"missing version slot for vertex seq={vertexId.Sequence}");
        }
        var fields = rec.Slice(HdrSize, PayloadSize);
        return new VertexWriteHandle(_file, pageId, fields);
    }

    public IEnumerable<VertexId> Scan()
    {
        long hwm = _map.Hwm;
        for (long seq = 0; seq < hwm; seq++)
        {
            // 可視性は heap version で判定する (null head / 不可視は false)。
            if (_heap.TryReadVisible(seq, AmbientVisible, out _, out _, out _))
            {
                MvccContext.RecordRead(EntityKind.Vertex, seq);
                yield return VertexId.Create(seq, CurrentGeneration(seq));
            }
        }
    }

    public int CurrentGeneration(long localId)
    {
        if (localId < 0 || localId >= _map.Hwm) return -1;
        // gen-stamp-fastpath: 再利用が一度も起きていなければ [0, Hwm) の全 slot は generation = 1
        // (monotonic 採番 + 再利用なし)。per-row の version sidecar read を省く。
        if (!_anyReuse) return 1;
        long gen = _versions.Read(localId).Generation;
        return gen > int.MaxValue ? int.MaxValue : (int)gen;
    }

    public PropertyCursor EnumerateProperties(VertexId vertexId, IPropertyStore overflowStore)
    {
        if (!_heap.TryReadVisible(vertexId.Sequence, AmbientVisible, out var payload, out _, out _))
            return new PropertyCursor(overflowStore, default, PropertyVersionRef.Invalid);
        MvccContext.RecordRead(EntityKind.Vertex, vertexId.Sequence); // property 列挙 = vertex read
        var firstProp = new PropertyVersionRef(RecordHelpers.ReadInt48(payload.AsSpan(OffFirstProp)));
        var ownerId = vertexId.Generation == 0
            ? VertexId.Create(vertexId.Sequence, CurrentGeneration(vertexId.Sequence))
            : vertexId;
        return overflowStore.Enumerate(EntityRef.From(ownerId), firstProp);
    }

    // --- internal helpers (EdgeStore fast-path / vacuum / bulk 経路用) ---

    internal void UpdateFirstEdgeId(VertexId vertexId, EdgeId newFirstEdgeId)
        => MutateHeadField(vertexId.Sequence, OffFirstEdge, newFirstEdgeId.Sequence);

    internal void UpdateFirstPropertyRef(VertexId vertexId, PropertyVersionRef newFirstPropertyRef)
        => MutateHeadField(vertexId.Sequence, OffFirstProp, newFirstPropertyRef.Sequence);

    internal EdgeId GetFirstEdgeId(VertexId vertexId)
    {
        var ptr = _heap.GetHead(vertexId.Sequence);
        if (ptr.IsNull) return EdgeId.Invalid;
        using var h = _file.PinForRead(new PageId(ptr.PageId));
        var sp = new ReadOnlySlottedPage(h.Data);
        if (!sp.TryGet(ptr.Slot, out var rec)) return EdgeId.Invalid;
        return new EdgeId(RecordHelpers.ReadInt48(rec[(HdrSize + OffFirstEdge)..]));
    }

    internal EdgeId GetFirstEdgeIdRaw(VertexId vertexId) => GetFirstEdgeId(vertexId);

    internal void UpdateFirstEdgeIdRaw(VertexId vertexId, EdgeId newFirstEdgeId)
        => UpdateFirstEdgeId(vertexId, newFirstEdgeId);

    /// <summary>vacuum: 可視性フィルタ無しの head version raw 読み取り。範囲外 / 未登録は default。</summary>
    internal RawVertexRecord ReadRaw(long id)
    {
        if (id < 0 || id >= _map.Hwm) return default;
        // xmin/xmax は sidecar ではなく heap version から読む。
        if (!_heap.TryReadHeadRaw(id, out var payload, out long xmin, out long xmax)) return default;
        var s = payload.AsSpan();
        return new RawVertexRecord
        {
            InUse = (s[OffFlags] & FlagInUse) != 0,
            FirstEdgeId = new EdgeId(RecordHelpers.ReadInt48(s[OffFirstEdge..])),
            FirstPropertyRef = new PropertyVersionRef(RecordHelpers.ReadInt48(s[OffFirstProp..])),
            Label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(s[OffLabel..])),
            Xmin = xmin,
            Xmax = xmax,
        };
    }

    internal void UpdateFirstPropertyRefRaw(VertexId vertexId, PropertyVersionRef newFirstPropertyRef)
        => UpdateFirstPropertyRef(vertexId, newFirstPropertyRef);

    /// <summary>採番済み Sequence 数 (= 最大 seq + 1)。</summary>
    internal long Hwm => _map.Hwm;

    /// <summary>テスト用。free list は持たない (monotonic seq)。</summary>
    internal long FreeHead => -1;

    /// <summary>
    /// heap モデルは物理 truncate での縮小をしない (slot が散在するため)。現ページ数を返し、
    /// vacuum の truncate を no-op にする。
    /// </summary>
    internal long ComputeRequiredPageCount() => _file.PageCount;

    /// <summary>内部 heap PagedFile。</summary>
    internal IPagedFile UnderlyingFile => _file;

    /// <summary>
    /// vacuum: horizon 未満で xmax がコミット済みの dead Vertexを heap から物理回収する
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
            else if (xmax == 0)
            {
                // 生存 Vertex では property 更新で生じた dead 旧版を回収する。
                _heap.PruneDeadVersions(seq,
                    (_, vx) => vx != 0 && vx < horizonTxId && committed.IsCommitted(vx));
            }
        }
        return reclaimed;
    }

    // --- bulk-load helpers (no per-record sidecar flush nuance; heap insert handles paging) ---

    internal void BulkWrite(long id, int labelId)
    {
        Span<byte> payload = stackalloc byte[PayloadSize];
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffFirstEdge..], -1L);
        RecordHelpers.WriteInt48(payload[OffFirstProp..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffLabel..], (short)labelId);
        _heap.Insert(id, payload, TransactionId.Bootstrap.Value);
        // bulk load は tx 外。Bootstrap を xmin に、Generation=1 (新規 slot)。
        _versions.Write(id, new EntityVersionMeta(TransactionId.Bootstrap.Value, 0, 0, long.MaxValue, 1));
    }

    internal void BulkUpdateFirstPropertyRef(long id, long firstPropertyRef)
        => MutateHeadField(id, OffFirstProp, firstPropertyRef);

    internal void BulkSetHeaders(long hwm, long inUseCount)
    {
        // hwm は map.Hwm が BulkWrite 経由で既に到達済み。inUse のみ採用。
        _inUseCount = inUseCount;
        _labelIndex?.Invalidate();
    }

    /// <summary>recovery 用: map メタを読み直し inUse を再計算する。</summary>
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
            // live は heap head が存在し xmax==0 の record とする。
            if (_heap.TryReadHeadRaw(seq, out _, out _, out long xmax) && xmax == 0)
                count++;
        return count;
    }
}

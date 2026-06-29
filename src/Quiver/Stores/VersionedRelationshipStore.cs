using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>vacuum: 可視性フィルタを通さない raw リレーションレコード (heap head 由来)。</summary>
internal struct RawRelRecord
{
    public bool InUse;
    public NodeId Source;
    public NodeId Target;
    public RelationshipTypeId Type;
    public RelationshipId SrcPrev;
    public RelationshipId SrcNext;
    public RelationshipId TgtPrev;
    public RelationshipId TgtNext;
    public PropertyId FirstProp;
    public long Xmin;
    public long Xmax;
}

/// <summary>
/// <see cref="VersionedRecordHeap"/> + <see cref="ItemPointerMap"/> 上に実装した
/// リレーションシップストア。<see cref="VersionedNodeStore"/> と同型で、固定 48B record 配列を
/// やめ可変長 slotted record にすることで Phase 4c の property inline 化の土台になる。
///
/// <para>リレーション payload (45B, 旧 <c>RelationshipStore</c> record と同形 — version ヘッダ 24B
/// の後ろ):</para>
/// <code>
///   [0]  flags     : u8     (FlagInUse)
///   [1]  source    : Int48  (Sequence)
///   [7]  target    : Int48  (Sequence)
///   [13] type      : i16
///   [15] srcPrev   : Int48  (Sequence)
///   [21] srcNext   : Int48  (Sequence)
///   [27] tgtPrev   : Int48  (Sequence)
///   [33] tgtNext   : Int48  (Sequence)
///   [39] firstProp : Int48  (Sequence)
/// </code>
/// 先頭 45B がそのまま <see cref="RelationshipWriteHandle"/> のレイアウトと一致するので in-place
/// 更新に再利用する。双方向 chain pointer (srcPrev/srcNext/tgtPrev/tgtNext) と firstProp は
/// **head version の in-place 更新** で書き換える (版を増やさない)。エッジ作成も版を増やさない。
///
/// <para><b>MVCC</b>: xmin/xmax は heap version ヘッダに保持する (<see cref="VersionedNodeStore"/>
/// Phase 3a と同じ統一レコードモデル)。<see cref="IEntityVersionStore"/> sidecar は Generation +
/// SSN (Pstamp/Sstamp) + commit 高水位のみを保持する。Sequence は vacuum 回収後に
/// <see cref="ItemPointerMap"/> の free list で再利用し、再利用ごとに世代を bump する
/// (ABA 検出維持)。</para>
/// </summary>
internal sealed class VersionedRelationshipStore : IRelationshipStore
{
    // 固定フィールド領域 (RelationshipWriteHandle が in-place 更新する先頭 45B)。
    private const int PayloadSize = 45;
    private const int OffFlags = 0;
    private const int OffSource = 1;
    private const int OffTarget = 7;
    private const int OffType = 13;
    private const int OffSrcPrev = 15;
    private const int OffSrcNext = 21;
    private const int OffTgtPrev = 27;
    private const int OffTgtNext = 33;
    private const int OffFirstProp = 39;
    private const byte FlagInUse = 0x01;
    // Phase 6 alloc-free read: 典型 inline payload (45 固定 + 数 entry) を収める stackalloc 量。
    // 超過分は割当版へフォールバックする。
    private const int InlineReadBuffer = 256;

    private static readonly int HdrSize = VersionedRecordHeap.VersionHeaderSize;

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private readonly VersionedRecordHeap _heap;
    private readonly IEntityVersionStore _versions;
    private long _inUseCount;

    public VersionedRelationshipStore(IPagedFile heapFile, ItemPointerMap map, IEntityVersionStore? versions = null)
    {
        _file = heapFile;
        _map = map;
        _heap = new VersionedRecordHeap(heapFile, map);
        _versions = versions ?? new InMemoryEntityVersionStore();
        _inUseCount = RecomputeInUse();
    }

    public long InUseCount => _inUseCount;

    public RelationshipId Create(INodeStore nodeStore, NodeId source, NodeId target, RelationshipTypeId type)
    {
        // 旧 RelationshipStore と同じく rel は Sequence 空間 (gen=0) で払い出す (rel に
        // 世代を surface しない。adjacency / chain pointer も Sequence 格納)。vacuum 回収済み seq は
        // map free list から再利用する。
        long seq = _map.PopFreeSeq();
        if (seq < 0) seq = _map.Hwm;
        var relId = new RelationshipId(seq);

        RelationshipId srcHead = GetFirstRelId(nodeStore, source);
        RelationshipId tgtHead = GetFirstRelId(nodeStore, target);

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload.Clear();
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffSource..], source.Sequence);
        RecordHelpers.WriteInt48(payload[OffTarget..], target.Sequence);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffType..], (short)type.Value);
        RecordHelpers.WriteInt48(payload[OffSrcPrev..], RelationshipId.Invalid.Sequence);
        RecordHelpers.WriteInt48(payload[OffSrcNext..], srcHead.Sequence);
        RecordHelpers.WriteInt48(payload[OffTgtPrev..], RelationshipId.Invalid.Sequence);
        RecordHelpers.WriteInt48(payload[OffTgtNext..], tgtHead.Sequence);
        RecordHelpers.WriteInt48(payload[OffFirstProp..], PropertyId.Invalid.Sequence);

        _heap.Insert(seq, payload, MvccContext.CurrentTxId.Value);
        _versions.Write(seq, new EntityVersionMeta(MvccContext.CurrentTxId.Value, 0, 0, long.MaxValue));
        _inUseCount++;

        // 旧 head の物理 prev を新 rel に向ける (双方向リンク維持。visibility は xmin/xmax で判定)。
        if (srcHead.IsValid)
            UpdateListPrev(srcHead, source, relId);
        if (tgtHead.IsValid && tgtHead != srcHead)
            UpdateListPrev(tgtHead, target, relId);

        SetFirstRelId(nodeStore, source, relId);
        SetFirstRelId(nodeStore, target, relId);
        return relId;
    }

    public void Delete(INodeStore nodeStore, RelationshipId relId)
    {
        // 論理削除のみ — head version に xmax をスタンプ。chain / slot は維持する
        // (snapshot reader が辿れるよう)。物理回収 + chain 整理は vacuum (OP-3)。
        _ = nodeStore;
        long seq = relId.Sequence;
        if (!_heap.TryReadHeadRaw(seq, out _, out _, out long xmax)) return;
        if (xmax != 0) return; // 既に論理削除済
        _heap.StampXmax(seq, MvccContext.CurrentTxId.Value);
        _inUseCount--;
    }

    public RelationshipReadHandle Read(RelationshipId relId)
    {
        long seq = relId.Sequence;
        if (seq < 0 || seq >= _map.Hwm)
            return NotInUse(relId);

        // 構造フィールド (endpoint / type / 双方向 chain pointer / firstProp) は **head version**
        // (物理最新, in-place 更新) から読む。これにより rel 自身が reader に不可視でも chain pointer
        // を返せ、RelationshipEnumerator が不可視 rel を skip して次へ進める (旧 RelationshipStore と
        // 同じセマンティクス。chain は物理一本で visibility は xmin/xmax で判定)。
        //
        // Task B (B2): head を **1 回の pin** で読み (alloc-free stackalloc)、可視性も head の
        // xmin/xmax から即判定する。head 可視 = 最頻ケース (単一版 / 可視 head) はここで確定し、
        // 旧実装の TryReadVisible 2 回目 pin + 破棄 ToArray を省く。head 不可視 & 多版の稀ケースのみ
        // 版チェーン走査へフォールバック。可視性セマンティクスは厳密に不変 (下記 3 分岐は
        // 「チェーンに可視版があるか」と完全等価)。
        Span<byte> span = stackalloc byte[PayloadSize];
        int len = _heap.TryReadHeadInto(seq, span, out long xmin, out long xmax, out bool hasOlderVersion);
        if (len == 0)
            return NotInUse(relId);

        var src = new NodeId(RecordHelpers.ReadInt48(span[OffSource..]));
        var tgt = new NodeId(RecordHelpers.ReadInt48(span[OffTarget..]));
        var type = new RelationshipTypeId(BinaryPrimitives.ReadInt16LittleEndian(span[OffType..]));
        var srcPrev = new RelationshipId(RecordHelpers.ReadInt48(span[OffSrcPrev..]));
        var srcNext = new RelationshipId(RecordHelpers.ReadInt48(span[OffSrcNext..]));
        var tgtPrev = new RelationshipId(RecordHelpers.ReadInt48(span[OffTgtPrev..]));
        var tgtNext = new RelationshipId(RecordHelpers.ReadInt48(span[OffTgtNext..]));
        var firstProp = new PropertyId(RecordHelpers.ReadInt48(span[OffFirstProp..]));

        // InUse = (slot 有効) かつ「版チェーンに reader から見える版がある」。
        //   head 可視                       → 可視 (TryReadVisible が head で即 true を返すのと等価)
        //   head 不可視 & 単一版            → 不可視 (チェーンに他の版が無い)
        //   head 不可視 & 多版              → 版チェーン走査 (旧経路と同一: 最初の可視版を探す)
        bool inUse;
        if ((span[OffFlags] & FlagInUse) == 0)
            inUse = false;
        else if (AmbientVisible(xmin, xmax))
            inUse = true;
        else if (!hasOlderVersion)
            inUse = false;
        else
            inUse = _heap.TryReadVisible(seq, AmbientVisible, out _, out _, out _);

        if (inUse) MvccContext.RecordRead(EntityKind.Relationship, seq);
        return new RelationshipReadHandle(relId, inUse, src, tgt, type, srcPrev, srcNext, tgtPrev, tgtNext, firstProp);
    }

    public RelationshipWriteHandle Write(RelationshipId relId)
    {
        var ptr = _heap.GetHead(relId.Sequence);
        if (ptr.IsNull)
            throw new CorruptionException($"Write on missing relationship seq={relId.Sequence}");
        var pageId = new PageId(ptr.PageId);
        var ph = _file.PinForWrite(pageId);
        var sp = new SlottedPage(ph.Data);
        if (!sp.TryGetMutable(ptr.Slot, out var rec))
        {
            _file.Unpin(pageId);
            throw new CorruptionException($"missing version slot for relationship seq={relId.Sequence}");
        }
        var fields = rec.Slice(HdrSize, PayloadSize);
        return new RelationshipWriteHandle(_file, pageId, fields);
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore)
        => new RelationshipEnumerator(this, nodeId, GetFirstRelId(nodeStore, nodeId));

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore,
        RelationshipTypeId type, Direction direction)
        => new RelationshipEnumerator(this, nodeId, GetFirstRelId(nodeStore, nodeId), type, direction);

    public IEnumerable<RelationshipId> Scan()
    {
        long hwm = _map.Hwm;
        for (long seq = 0; seq < hwm; seq++)
        {
            if (_heap.TryReadVisible(seq, AmbientVisible, out _, out _, out _))
            {
                MvccContext.RecordRead(EntityKind.Relationship, seq);
                yield return new RelationshipId(seq);
            }
        }
    }

    // ===== inline property storage (rel 粒度 copy-on-write) =====
    // 符号化は InlinePropertyCodec (RelFixedSize=45) に集約。node 側と同じ copy-on-write 機構。

    public bool TryGetInlineProperty(RelationshipId relId, PropertyKeyId keyId, out PropertyValue value)
    {
        value = default;
        long seq = relId.Sequence;
        // alloc-free 経路。可視版 payload を stackalloc バッファへコピーして scan する
        // (per-read の byte[] 割当を回避)。scalar は値コピーなので buffer 上 decode で安全、String/Bytes
        // のみ安定 byte[] へコピーする。payload が buffer 超過なら割当版へフォールバック。
        Span<byte> buf = stackalloc byte[InlineReadBuffer];
        int len = _heap.TryReadVisibleInto(seq, AmbientVisible, buf, out _, out _);
        if (len == 0) return false;
        // property read = rel read。可視版を観測したので SSN read-set に記録する。
        MvccContext.RecordRead(EntityKind.Relationship, seq);
        if (len <= buf.Length)
        {
            if (!InlinePropertyCodec.TryScan(buf[..len], InlinePropertyCodec.RelFixedSize, keyId.Value, out var type, out var span))
                return false;
            value = InlinePropertyCodec.IsScalar(type)
                ? InlinePropertyCodec.DecodeScalar(type, span)
                : InlinePropertyCodec.Decode(type, span.ToArray());
            return true;
        }
        if (!_heap.TryReadVisible(seq, AmbientVisible, out var payload, out _, out _)) return false;
        if (!InlinePropertyCodec.TryScan(payload, InlinePropertyCodec.RelFixedSize, keyId.Value, out var t2, out var s2)) return false;
        value = InlinePropertyCodec.Decode(t2, s2);
        return true;
    }

    public bool HasInlineProperty(RelationshipId relId, PropertyKeyId keyId)
    {
        long seq = relId.Sequence;
        Span<byte> buf = stackalloc byte[InlineReadBuffer];
        int len = _heap.TryReadVisibleInto(seq, AmbientVisible, buf, out _, out _);
        if (len == 0) return false;
        MvccContext.RecordRead(EntityKind.Relationship, seq);
        if (len <= buf.Length)
            return InlinePropertyCodec.TryScan(buf[..len], InlinePropertyCodec.RelFixedSize, keyId.Value, out _, out _);
        if (!_heap.TryReadVisible(seq, AmbientVisible, out var payload, out _, out _)) return false;
        return InlinePropertyCodec.TryScan(payload, InlinePropertyCodec.RelFixedSize, keyId.Value, out _, out _);
    }

    public bool SetInlineProperty(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value)
    {
        if (!InlinePropertyCodec.IsInlineable(value)) return false;
        long seq = relId.Sequence;
        if (!_heap.TryReadVisible(seq, AmbientVisible, out var cur, out _, out _)) return false;
        byte[] np = InlinePropertyCodec.Build(cur, InlinePropertyCodec.RelFixedSize, keyId.Value, in value, remove: false);
        if (np.Length > VersionedRecordHeap.MaxPayloadSize) return false; // payload 予算超過 → overflow
        _heap.AppendOrReplaceHead(seq, np, MvccContext.CurrentTxId.Value);
        return true;
    }

    public bool RemoveInlineProperty(RelationshipId relId, PropertyKeyId keyId)
    {
        long seq = relId.Sequence;
        if (!_heap.TryReadVisible(seq, AmbientVisible, out var cur, out _, out _)) return false;
        if (!InlinePropertyCodec.TryScan(cur, InlinePropertyCodec.RelFixedSize, keyId.Value, out _, out _)) return false;
        byte[] np = InlinePropertyCodec.Build(cur, InlinePropertyCodec.RelFixedSize, keyId.Value, default, remove: true);
        _heap.AppendOrReplaceHead(seq, np, MvccContext.CurrentTxId.Value);
        return true;
    }

    public PropertyEnumerator EnumerateProperties(RelationshipId relId, IPropertyStore overflowStore)
    {
        if (!_heap.TryReadVisible(relId.Sequence, AmbientVisible, out var payload, out _, out _))
            return new PropertyEnumerator(overflowStore, PropertyId.Invalid);
        MvccContext.RecordRead(EntityKind.Relationship, relId.Sequence); // property 列挙 = rel read
        var firstProp = new PropertyId(RecordHelpers.ReadInt48(payload.AsSpan(OffFirstProp)));
        return new PropertyEnumerator(payload, overflowStore, firstProp, InlinePropertyCodec.RelFixedSize);
    }

    // --- internal bulk-load helpers (no MvccContext; heap insert handles paging) ---

    internal void BulkWrite(long id, long src, long tgt, int typeId,
        long srcPrev, long srcNext, long tgtPrev, long tgtNext)
    {
        Span<byte> payload = stackalloc byte[PayloadSize];
        payload.Clear();
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffSource..], src);
        RecordHelpers.WriteInt48(payload[OffTarget..], tgt);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffType..], (short)typeId);
        RecordHelpers.WriteInt48(payload[OffSrcPrev..], srcPrev);
        RecordHelpers.WriteInt48(payload[OffSrcNext..], srcNext);
        RecordHelpers.WriteInt48(payload[OffTgtPrev..], tgtPrev);
        RecordHelpers.WriteInt48(payload[OffTgtNext..], tgtNext);
        RecordHelpers.WriteInt48(payload[OffFirstProp..], -1L);
        _heap.Insert(id, payload, TransactionId.Bootstrap.Value);
        _versions.Write(id, new EntityVersionMeta(TransactionId.Bootstrap.Value, 0, 0, long.MaxValue, 1));
    }

    internal void BulkSetHeaders(long hwm, long inUseCount)
    {
        // hwm は map.Hwm が BulkWrite 経由で既に到達済み。inUse のみ採用。
        _ = hwm;
        _inUseCount = inUseCount;
    }

    /// <summary>recovery 用: map / heap メタを読み直し inUse を再計算する。</summary>
    internal void ReloadMeta()
    {
        _map.ReloadMeta();
        _heap.ReloadMeta();
        _inUseCount = RecomputeInUse();
    }

    /// <summary>採番済み Sequence 数 (= 最大 seq + 1)。</summary>
    internal long Hwm => _map.Hwm;

    /// <summary>
    /// seq の現世代を返す (ベクトル binding の slot 再利用検出用)。範囲外は -1。
    /// <see cref="VersionedNodeStore.CurrentGeneration"/> と同形。
    /// </summary>
    public int CurrentGeneration(long localId)
    {
        if (localId < 0 || localId >= _map.Hwm) return -1;
        long gen = _versions.Read(localId).Generation;
        return gen > int.MaxValue ? int.MaxValue : (int)gen;
    }

    /// <summary>テスト用。free list は ItemPointerMap が持つ。</summary>
    internal long FreeHead => _map.FreeHead;

    /// <summary>heap モデルは物理 truncate での縮小をしない (slot 散在)。現ページ数を返す。</summary>
    internal long ComputeRequiredPageCount() => _file.PageCount;

    /// <summary>内部 heap PagedFile。</summary>
    internal IPagedFile UnderlyingFile => _file;

    /// <summary>vacuum: 可視性フィルタ無しの head version raw 読み取り。範囲外 / 未登録は default。</summary>
    internal RawRelRecord ReadRaw(long seq)
    {
        if (seq < 0 || seq >= _map.Hwm) return default;
        if (!_heap.TryReadHeadRaw(seq, out var payload, out long xmin, out long xmax)) return default;
        var s = payload.AsSpan();
        return new RawRelRecord
        {
            InUse = (s[OffFlags] & FlagInUse) != 0,
            Source = new NodeId(RecordHelpers.ReadInt48(s[OffSource..])),
            Target = new NodeId(RecordHelpers.ReadInt48(s[OffTarget..])),
            Type = new RelationshipTypeId(BinaryPrimitives.ReadInt16LittleEndian(s[OffType..])),
            SrcPrev = new RelationshipId(RecordHelpers.ReadInt48(s[OffSrcPrev..])),
            SrcNext = new RelationshipId(RecordHelpers.ReadInt48(s[OffSrcNext..])),
            TgtPrev = new RelationshipId(RecordHelpers.ReadInt48(s[OffTgtPrev..])),
            TgtNext = new RelationshipId(RecordHelpers.ReadInt48(s[OffTgtNext..])),
            FirstProp = new PropertyId(RecordHelpers.ReadInt48(s[OffFirstProp..])),
            Xmin = xmin,
            Xmax = xmax,
        };
    }

    /// <summary>
    /// vacuum: ノードストアと協調して双方向 chain を再構築し、dead version を物理回収する。
    /// 手順は旧 <c>RelationshipStore.VacuumDeadVersions</c> と同じ (heap 上で実施)。
    /// 呼び出し前提: アクティブトランザクション 0 件、ノード vacuum **前**。
    /// </summary>
    /// <returns>物理回収したリレーションシップ版数。</returns>
    internal int VacuumDeadVersions(VersionedNodeStore nodeStore, long horizonTxId, CommittedTxRegistry committed)
    {
        var reclaimSet = new HashSet<long>();

        // Pass 1: 各ノードの chain を rebuild。
        long nodeHwm = nodeStore.Hwm;
        for (long nid = 0; nid < nodeHwm; nid++)
        {
            var raw = nodeStore.ReadRaw(nid);
            if (!raw.InUse) continue;
            if (!raw.FirstRelId.IsValid) continue;

            bool nodeWillBeReclaimed = raw.Xmax != 0
                && raw.Xmax < horizonTxId
                && committed.IsCommitted(raw.Xmax);

            RebuildChainForNode(new NodeId(nid), raw.FirstRelId, nodeStore,
                horizonTxId, committed, reclaimSet, nodeWillBeReclaimed);
        }

        // Pass 2: 全 rel slot を走査し、まだ reclaim 集合に居ない dead rel を拾う。
        // live rel は inline property 更新の copy-on-write で生じた dead 旧版を prune する (node 3d 相当)。
        long hwm = _map.Hwm;
        for (long seq = 0; seq < hwm; seq++)
        {
            if (reclaimSet.Contains(seq)) continue;
            if (!_heap.TryReadHeadRaw(seq, out var payload, out _, out long xmax)) continue;
            bool inUse = (payload[OffFlags] & FlagInUse) != 0;
            if (!inUse) continue;
            bool dead = xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax);
            if (dead)
                reclaimSet.Add(seq);
            else if (xmax == 0)
                _heap.PruneDeadVersions(seq,
                    (_, vx) => vx != 0 && vx < horizonTxId && committed.IsCommitted(vx));
        }

        // Pass 3: reclaim 集合の slot を物理 free。
        foreach (var seq in reclaimSet)
        {
            _heap.Remove(seq);
            _map.PushFreeSeq(seq);
        }

        return reclaimSet.Count;
    }

    private void RebuildChainForNode(NodeId node, RelationshipId head,
        VersionedNodeStore nodeStore, long horizonTxId, CommittedTxRegistry committed,
        HashSet<long> reclaimSet, bool nodeWillBeReclaimed)
    {
        var entries = new List<(RelationshipId Id, bool NodeIsSource, bool Dead)>();
        var cur = head;
        long guard = _map.Hwm + 1;
        while (cur.IsValid && guard-- > 0)
        {
            if (!_heap.TryReadHeadRaw(cur.Sequence, out var payload, out _, out long xmax))
                break;
            var s = payload.AsSpan();
            bool inUse = (s[OffFlags] & FlagInUse) != 0;
            long src = RecordHelpers.ReadInt48(s[OffSource..]);
            long tgt = RecordHelpers.ReadInt48(s[OffTarget..]);
            bool nodeIsSource = src == node.Sequence;
            bool nodeIsTarget = tgt == node.Sequence;
            if (!nodeIsSource && !nodeIsTarget)
                break; // chain 整合性が崩れている → 打ち切る
            RelationshipId nextOnThisSide = nodeIsSource
                ? new RelationshipId(RecordHelpers.ReadInt48(s[OffSrcNext..]))
                : new RelationshipId(RecordHelpers.ReadInt48(s[OffTgtNext..]));
            bool dead = !inUse
                || (xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax));
            entries.Add((cur, nodeIsSource, dead));
            if (dead) reclaimSet.Add(cur.Sequence);
            cur = nextOnThisSide;
        }

        if (nodeWillBeReclaimed)
            return; // ノード自体が消えるので chain head 更新は不要。

        // live ノード: live entries だけ残して chain を再構築。
        RelationshipId newHead = RelationshipId.Invalid;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (id, nodeIsSource, dead) = entries[i];
            if (dead) continue;
            int prevOff = nodeIsSource ? OffSrcPrev : OffTgtPrev;
            int nextOff = nodeIsSource ? OffSrcNext : OffTgtNext;
            WriteHeadInt48(id.Sequence, prevOff, RelationshipId.Invalid.Sequence);
            WriteHeadInt48(id.Sequence, nextOff, newHead.Sequence);
            newHead = id;
        }

        // 各 live の prev を正しく書き直す (上のループは prev=Invalid 固定だったため)。
        RelationshipId prev = RelationshipId.Invalid;
        foreach (var (id, nodeIsSource, dead) in entries)
        {
            if (dead) continue;
            int prevOff = nodeIsSource ? OffSrcPrev : OffTgtPrev;
            WriteHeadInt48(id.Sequence, prevOff, prev.Sequence);
            prev = id;
        }

        nodeStore.UpdateFirstRelIdRaw(node, newHead);
    }

    // --- private helpers ---

    private static bool AmbientVisible(long xmin, long xmax) => Visibility.IsVisibleAmbient(xmin, xmax);

    private static RelationshipReadHandle NotInUse(RelationshipId relId)
        => new RelationshipReadHandle(
            relId, inUse: false, default, default, default,
            RelationshipId.Invalid, RelationshipId.Invalid,
            RelationshipId.Invalid, RelationshipId.Invalid,
            PropertyId.Invalid);

    private static RelationshipId GetFirstRelId(INodeStore nodeStore, NodeId nodeId)
    {
        if (nodeStore is VersionedNodeStore ns) return ns.GetFirstRelId(nodeId);
        using var h = nodeStore.Read(nodeId);
        return h.FirstRelationshipId;
    }

    private static void SetFirstRelId(INodeStore nodeStore, NodeId nodeId, RelationshipId relId)
    {
        if (nodeStore is VersionedNodeStore ns)
        {
            ns.UpdateFirstRelId(nodeId, relId);
            return;
        }
        var w = nodeStore.Write(nodeId);
        w.FirstRelationshipId = relId;
        w.Dispose();
    }

    /// <summary>head version の chain prev pointer (この rel が <paramref name="side"/> 側) を in-place 更新。</summary>
    private void UpdateListPrev(RelationshipId relId, NodeId side, RelationshipId newPrev)
    {
        var ptr = _heap.GetHead(relId.Sequence);
        if (ptr.IsNull) return;
        var pageId = new PageId(ptr.PageId);
        using var ph = _file.PinForWrite(pageId);
        var sp = new SlottedPage(ph.Data);
        if (!sp.TryGetMutable(ptr.Slot, out var rec)) return;
        var payload = rec.Slice(HdrSize, PayloadSize);
        NodeId recSrc = new(RecordHelpers.ReadInt48(payload[OffSource..]));
        int prevOff = recSrc == side ? OffSrcPrev : OffTgtPrev;
        RecordHelpers.WriteInt48(payload[prevOff..], newPrev.Sequence);
    }

    private void WriteHeadInt48(long seq, int payloadOffset, long sequenceValue)
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

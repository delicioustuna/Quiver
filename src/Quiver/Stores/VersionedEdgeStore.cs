using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>vacuum: 可視性フィルタを通さない raw リレーションレコード (heap head 由来)。</summary>
internal struct RawEdgeRecord
{
    public bool InUse;
    public VertexId Source;
    public VertexId Target;
    public EdgeTypeId Type;
    public EdgeId SrcPrev;
    public EdgeId SrcNext;
    public EdgeId TgtPrev;
    public EdgeId TgtNext;
    public PropertyVersionRef FirstPropertyRef;
    public long Xmin;
    public long Xmax;
}

/// <summary>
/// <see cref="VersionedRecordHeap"/> + <see cref="ItemPointerMap"/> 上に実装した
/// Edgeストア。<see cref="VersionedVertexStore"/> と同型で、固定 48B record 配列を
/// やめ versioned slotted record に格納する。
///
/// <para>リレーション payload (45B, 旧 <c>EdgeStore</c> record と同形 — version ヘッダ 24B
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
/// 先頭 45B がそのまま <see cref="EdgeWriteHandle"/> のレイアウトと一致するので in-place
/// 更新に再利用する。双方向 chain pointer (srcPrev/srcNext/tgtPrev/tgtNext) と firstProp は
/// **head version の in-place 更新** で書き換える (版を増やさない)。エッジ作成も版を増やさない。
///
/// <para><b>MVCC</b>: xmin/xmax は heap version ヘッダに保持する (<see cref="VersionedVertexStore"/>
/// Vertex と同じ統一レコードモデル)。<see cref="IEntityVersionStore"/> sidecar は Generation +
/// MVCC (xmin/xmax) + Generation を保持する。edge の raw Sequence は
/// adjacency、delta、locator、epoch entry に残り得るため、再利用解放 coordinator がそれらを
/// 除去するまで free list へ戻さない。物理ページの回収と logical Sequence の再利用を混同すると、
/// raw entry が別 edge を指す ABA になる。</para>
/// </summary>
internal sealed class VersionedEdgeStore : IEdgeStore, ITransactionEdgeStore
{
    // 固定フィールド領域 (EdgeWriteHandle が in-place 更新する先頭 45B)。
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
    private static readonly int HdrSize = VersionedRecordHeap.VersionHeaderSize;

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private readonly VersionedRecordHeap _heap;
    private readonly IEntityVersionStore _versions;
    private readonly EdgeLocatorStore? _locators;
    private long _inUseCount;
    // raw adjacency、delta、locator が Sequence を保持する間は edge Sequence を再利用しない。
    // sidecar に再利用履歴がなければ全採番済み slot の generation は 1 なので、
    // logical output ごとの sidecar read を省ける。
    private bool _anyReuse;

    public VersionedEdgeStore(
        IPagedFile heapFile,
        ItemPointerMap map,
        IEntityVersionStore? versions = null,
        EdgeLocatorStore? locators = null)
    {
        _file = heapFile;
        _map = map;
        _heap = new VersionedRecordHeap(heapFile, map);
        _versions = versions ?? new InMemoryEntityVersionStore();
        _locators = locators;
        BackfillLocators();
        _inUseCount = RecomputeInUse();
        _anyReuse = _versions.AnyGenerationReuse;
    }

    public long InUseCount => _inUseCount;

    public EdgeId Create(IVertexStore vertexStore, VertexId source, VertexId target, EdgeTypeId type)
        => Create(vertexStore, source, target, type, TransactionId.Bootstrap);

    public EdgeId Create(
        IVertexStore vertexStore,
        VertexId source,
        VertexId target,
        EdgeTypeId type,
        TransactionId transactionId)
    {
        // raw adjacency / delta / locator / epoch entry が残る間に slot を再利用すると、
        // entry の Sequence が別 edge を指す。再利用解放 coordinator が lifecycle を
        // 完結させるまでは free 候補を見ず high-water mark からだけ採番する。
        long seq = _map.Hwm;
        long generation = _versions.Read(seq).Generation + 1;
        var edgeId = EdgeId.Create(seq, checked((int)generation));

        EdgeId srcHead = GetFirstEdgeId(vertexStore, source);
        EdgeId tgtHead = GetFirstEdgeId(vertexStore, target);

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload.Clear();
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffSource..], source.Sequence);
        RecordHelpers.WriteInt48(payload[OffTarget..], target.Sequence);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffType..], (short)type.Value);
        RecordHelpers.WriteInt48(payload[OffSrcPrev..], EdgeId.Invalid.Sequence);
        RecordHelpers.WriteInt48(payload[OffSrcNext..], srcHead.Sequence);
        RecordHelpers.WriteInt48(payload[OffTgtPrev..], EdgeId.Invalid.Sequence);
        RecordHelpers.WriteInt48(payload[OffTgtNext..], tgtHead.Sequence);
        RecordHelpers.WriteInt48(payload[OffFirstProp..], PropertyVersionRef.Invalid.Sequence);

        _heap.Insert(seq, payload, transactionId.Value);
        _versions.Write(seq, new EntityVersionMeta(transactionId.Value, 0, generation));
        _locators?.WriteLive(seq, checked((int)generation), seq, source, target, type);
        _inUseCount++;

        // 旧 head の物理 prev を新 edge に向ける (双方向リンク維持。visibility は xmin/xmax で判定)。
        if (srcHead.IsValid)
            UpdateListPrev(srcHead, source, edgeId);
        if (tgtHead.IsValid && tgtHead != srcHead)
            UpdateListPrev(tgtHead, target, edgeId);

        SetFirstEdgeId(vertexStore, source, edgeId);
        SetFirstEdgeId(vertexStore, target, edgeId);
        return edgeId;
    }

    public void Delete(IVertexStore vertexStore, EdgeId edgeId)
        => Delete(vertexStore, edgeId, TransactionId.Bootstrap);

    public void Delete(IVertexStore vertexStore, EdgeId edgeId, TransactionId transactionId)
    {
        // 論理削除のみ — head version に xmax をスタンプ。chain / slot は維持する
        // (snapshot reader が辿れるよう)。物理回収 + chain 整理は vacuum (OP-3)。
        _ = vertexStore;
        if (!TryResolveRecordSequence(edgeId, out long seq, requireLive: true)) return;
        if (!_heap.TryReadHeadRaw(seq, out _, out _, out long xmax)) return;
        if (xmax != 0) return; // 既に論理削除済
        _heap.StampXmax(seq, transactionId.Value);
        int generation = CurrentGeneration(edgeId.Sequence);
        if (generation >= 0)
            _locators?.WriteDeleted(edgeId.Sequence, generation);
        _inUseCount--;
    }

    public EdgeReadHandle Read(EdgeId edgeId)
        => Read(edgeId, LatestVisible);

    public EdgeReadHandle Read(EdgeId edgeId, VersionVisible visibility)
    {
        if (!TryResolveRecordSequence(edgeId, out long seq))
            return NotInUse(edgeId);
        if (seq < 0 || seq >= _map.Hwm)
            return NotInUse(edgeId);

        // 構造フィールド (endpoint / type / 双方向 chain pointer / firstProp) は **head version**
        // (物理最新, in-place 更新) から読む。これにより edge 自身が reader に不可視でも chain pointer
        // を返せ、EdgeEnumerator が不可視 edge を skip して次へ進める (旧 EdgeStore と
        // 同じセマンティクス。chain は物理一本で visibility は xmin/xmax で判定)。
        //
        // head を 1 回の pin で読み、可視性も同じ version header から判定する。
        // xmin/xmax から即判定する。head 可視 = 最頻ケース (単一版 / 可視 head) はここで確定し、
        // 旧実装の TryReadVisible 2 回目 pin + 破棄 ToArray を省く。head 不可視 & 多版の稀ケースのみ
        // 版チェーン走査へフォールバック。可視性セマンティクスは厳密に不変 (下記 3 分岐は
        // 「チェーンに可視版があるか」と完全等価)。
        Span<byte> span = stackalloc byte[PayloadSize];
        int len = _heap.TryReadHeadInto(seq, span, out long xmin, out long xmax, out bool hasOlderVersion);
        if (len == 0)
            return NotInUse(edgeId);

        var src = new VertexId(RecordHelpers.ReadInt48(span[OffSource..]));
        var tgt = new VertexId(RecordHelpers.ReadInt48(span[OffTarget..]));
        var type = new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(span[OffType..]));
        var srcPrev = new EdgeId(RecordHelpers.ReadInt48(span[OffSrcPrev..]));
        var srcNext = new EdgeId(RecordHelpers.ReadInt48(span[OffSrcNext..]));
        var tgtPrev = new EdgeId(RecordHelpers.ReadInt48(span[OffTgtPrev..]));
        var tgtNext = new EdgeId(RecordHelpers.ReadInt48(span[OffTgtNext..]));
        var firstProp = new PropertyVersionRef(RecordHelpers.ReadInt48(span[OffFirstProp..]));

        // InUse = (slot 有効) かつ「版チェーンに reader から見える版がある」。
        //   head 可視                       → 可視 (TryReadVisible が head で即 true を返すのと等価)
        //   head 不可視 & 単一版            → 不可視 (チェーンに他の版が無い)
        //   head 不可視 & 多版              → 版チェーン走査 (旧経路と同一: 最初の可視版を探す)
        bool inUse;
        if ((span[OffFlags] & FlagInUse) == 0)
            inUse = false;
        else if (visibility(xmin, xmax))
            inUse = true;
        else if (!hasOlderVersion)
            inUse = false;
        else
            inUse = _heap.TryReadVisible(seq, visibility, out _, out _, out _);

        var resolvedId = EdgeId.Create(seq, CurrentGeneration(seq));
        return new EdgeReadHandle(resolvedId, inUse, src, tgt, type, srcPrev, srcNext, tgtPrev, tgtNext, firstProp);
    }

    public EdgeWriteHandle Write(EdgeId edgeId)
    {
        if (!TryResolveRecordSequence(edgeId, out long seq, requireLive: true))
            throw new CorruptionException($"Write on missing edge seq={edgeId.Sequence}");
        var ptr = _heap.GetHead(seq);
        if (ptr.IsNull)
            throw new CorruptionException($"Write on missing edge seq={seq}");
        var pageId = new PageId(ptr.PageId);
        var ph = _file.PinForWrite(pageId);
        var sp = new SlottedPage(ph.Data);
        if (!sp.TryGetMutable(ptr.Slot, out var rec))
        {
            _file.Unpin(pageId);
            throw new CorruptionException($"missing version slot for edge seq={seq}");
        }
        var fields = rec.Slice(HdrSize, PayloadSize);
        return new EdgeWriteHandle(_file, pageId, fields);
    }

    public EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore vertexStore)
        => new EdgeEnumerator(this, vertexStore, vertexId, GetFirstEdgeId(vertexStore, vertexId));

    public EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore vertexStore,
        EdgeTypeId type, Direction direction)
        => new EdgeEnumerator(this, vertexStore, vertexId, GetFirstEdgeId(vertexStore, vertexId), type, direction);

    public IEnumerable<EdgeId> Scan()
        => Scan(LatestVisible);

    public IEnumerable<EdgeId> Scan(VersionVisible visibility)
    {
        long hwm = _map.Hwm;
        for (long seq = 0; seq < hwm; seq++)
        {
            if (_heap.TryReadVisible(seq, visibility, out _, out _, out _))
            {
                yield return EdgeId.Create(seq, CurrentGeneration(seq));
            }
        }
    }

    public PropertyCursor EnumerateProperties(EdgeId edgeId, IPropertyStore overflowStore)
    {
        if (!TryResolveRecordSequence(edgeId, out long seq) ||
            !_heap.TryReadVisible(seq, LatestVisible, out var payload, out _, out _))
            return new PropertyCursor(overflowStore, default, PropertyVersionRef.Invalid);
        var firstProp = new PropertyVersionRef(RecordHelpers.ReadInt48(payload.AsSpan(OffFirstProp)));
        var ownerId = edgeId.Generation == 0
            ? EdgeId.Create(edgeId.Sequence, CurrentGeneration(edgeId.Sequence))
            : edgeId;
        return overflowStore.Enumerate(EntityRef.From(ownerId), firstProp);
    }

    // --- internal bulk-load helpers (bootstrap TxId; heap insert handles paging) ---

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
        _versions.Write(id, new EntityVersionMeta(TransactionId.Bootstrap.Value, 0, 1));
        _locators?.WriteLive(
            id,
            1,
            id,
            new VertexId(src),
            new VertexId(tgt),
            new EdgeTypeId(typeId));
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
        _locators?.ReloadMeta();
        BackfillLocators();
        _inUseCount = RecomputeInUse();
        _anyReuse = _versions.AnyGenerationReuse;
    }

    /// <summary>採番済み Sequence 数 (= 最大 seq + 1)。</summary>
    internal long Hwm => _map.Hwm;

    /// <summary>
    /// seq の現世代を返す (ベクトル binding の slot 再利用検出用)。範囲外は -1。
    /// <see cref="VersionedVertexStore.CurrentGeneration"/> と同形。
    /// </summary>
    public int CurrentGeneration(long localId)
    {
        if (localId < 0 || localId >= _map.Hwm) return -1;
        if (!_anyReuse) return 1;
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
    internal RawEdgeRecord ReadRaw(long seq)
    {
        if (seq < 0 || seq >= _map.Hwm) return default;
        if (!_heap.TryReadHeadRaw(seq, out var payload, out long xmin, out long xmax)) return default;
        var s = payload.AsSpan();
        return new RawEdgeRecord
        {
            InUse = (s[OffFlags] & FlagInUse) != 0,
            Source = new VertexId(RecordHelpers.ReadInt48(s[OffSource..])),
            Target = new VertexId(RecordHelpers.ReadInt48(s[OffTarget..])),
            Type = new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(s[OffType..])),
            SrcPrev = new EdgeId(RecordHelpers.ReadInt48(s[OffSrcPrev..])),
            SrcNext = new EdgeId(RecordHelpers.ReadInt48(s[OffSrcNext..])),
            TgtPrev = new EdgeId(RecordHelpers.ReadInt48(s[OffTgtPrev..])),
            TgtNext = new EdgeId(RecordHelpers.ReadInt48(s[OffTgtNext..])),
            FirstPropertyRef = new PropertyVersionRef(RecordHelpers.ReadInt48(s[OffFirstProp..])),
            Xmin = xmin,
            Xmax = xmax,
        };
    }

    /// <summary>
    /// vacuum: Vertexストアと協調して双方向 chain を再構築し、dead version を物理回収する。
    /// 手順は旧 <c>EdgeStore.VacuumDeadVersions</c> と同じ (heap 上で実施)。
    /// 呼び出し前提: アクティブトランザクション 0 件、Vertex vacuum **前**。
    /// </summary>
    /// <returns>物理回収したEdge版数。</returns>
    internal int VacuumDeadVersions(VersionedVertexStore vertexStore, long horizonTxId, CommittedTxRegistry committed)
    {
        var reclaimSet = new HashSet<long>();

        // Pass 1: 各Vertexの chain を rebuild。
        long vertexHwm = vertexStore.Hwm;
        for (long nid = 0; nid < vertexHwm; nid++)
        {
            var raw = vertexStore.ReadRaw(nid);
            if (!raw.InUse) continue;
            if (!raw.FirstEdgeId.IsValid) continue;

            bool vertexWillBeReclaimed = raw.Xmax != 0
                && raw.Xmax < horizonTxId
                && committed.IsCommitted(raw.Xmax);

            RebuildChainForVertex(new VertexId(nid), raw.FirstEdgeId, vertexStore,
                horizonTxId, committed, reclaimSet, vertexWillBeReclaimed);
        }

        // Pass 2: 全 edge slot を走査し、まだ reclaim 集合に居ない dead edge を拾う。
        // live edge は inline property 更新の copy-on-write で生じた dead 旧版を prune する (vertex 3d 相当)。
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

        // Pass 3: reclaim 集合の record を物理回収する。
        // raw derived entry が残るため、ここで map free list へ Sequence を release してはならない。
        // 再利用解放は base rebuild、delta/epoch reset、locator rebuild、derived durable を完了した
        // maintenance coordinator だけが担う。
        foreach (var seq in reclaimSet)
        {
            _heap.Remove(seq);
        }

        return reclaimSet.Count;
    }

    private void RebuildChainForVertex(VertexId vertex, EdgeId head,
        VersionedVertexStore vertexStore, long horizonTxId, CommittedTxRegistry committed,
        HashSet<long> reclaimSet, bool vertexWillBeReclaimed)
    {
        var entries = new List<(EdgeId Id, bool VertexIsSource, bool Dead)>();
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
            bool vertexIsSource = src == vertex.Sequence;
            bool vertexIsTarget = tgt == vertex.Sequence;
            if (!vertexIsSource && !vertexIsTarget)
                break; // chain 整合性が崩れている → 打ち切る
            EdgeId nextOnThisSide = vertexIsSource
                ? new EdgeId(RecordHelpers.ReadInt48(s[OffSrcNext..]))
                : new EdgeId(RecordHelpers.ReadInt48(s[OffTgtNext..]));
            bool dead = !inUse
                || (xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax));
            entries.Add((cur, vertexIsSource, dead));
            if (dead) reclaimSet.Add(cur.Sequence);
            cur = nextOnThisSide;
        }

        if (vertexWillBeReclaimed)
            return; // Vertex自体が消えるので chain head 更新は不要。

        // live Vertex: live entries だけ残して chain を再構築。
        EdgeId newHead = EdgeId.Invalid;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (id, vertexIsSource, dead) = entries[i];
            if (dead) continue;
            int prevOff = vertexIsSource ? OffSrcPrev : OffTgtPrev;
            int nextOff = vertexIsSource ? OffSrcNext : OffTgtNext;
            WriteHeadInt48(id.Sequence, prevOff, EdgeId.Invalid.Sequence);
            WriteHeadInt48(id.Sequence, nextOff, newHead.Sequence);
            newHead = id;
        }

        // 各 live の prev を正しく書き直す (上のループは prev=Invalid 固定だったため)。
        EdgeId prev = EdgeId.Invalid;
        foreach (var (id, vertexIsSource, dead) in entries)
        {
            if (dead) continue;
            int prevOff = vertexIsSource ? OffSrcPrev : OffTgtPrev;
            WriteHeadInt48(id.Sequence, prevOff, prev.Sequence);
            prev = id;
        }

        vertexStore.UpdateFirstEdgeIdRaw(vertex, newHead);
    }

    // --- private helpers ---

    private static bool LatestVisible(long xmin, long xmax) => xmin != 0 && xmax == 0;

    private bool TryResolveRecordSequence(
        EdgeId edgeId,
        out long recordSequence,
        bool requireLive = false)
    {
        recordSequence = edgeId.Sequence;
        if (_locators == null)
            return edgeId.IsValid;

        if (!_locators.TryRead(edgeId.Sequence, out var locator))
            return false;

        int carriedGeneration = edgeId.Generation;
        if (carriedGeneration != 0 && carriedGeneration != locator.Generation)
            return false;
        if (requireLive && !locator.Live)
            return false;
        if (locator.RecordSequence < 0)
            return false;
        recordSequence = locator.RecordSequence;
        return true;
    }

    private void BackfillLocators()
    {
        if (_locators == null || _locators.Hwm >= _map.Hwm)
            return;

        long hwm = _map.Hwm;
        for (long seq = _locators.Hwm; seq < hwm; seq++)
        {
            if (!_heap.TryReadHeadRaw(seq, out var payload, out _, out long xmax))
                continue;

            int generation = CurrentGeneration(seq);
            if (generation <= 0) generation = 1;
            var span = payload.AsSpan();
            var source = new VertexId(RecordHelpers.ReadInt48(span[OffSource..]));
            var target = new VertexId(RecordHelpers.ReadInt48(span[OffTarget..]));
            var type = new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(span[OffType..]));
            if (xmax == 0 && (span[OffFlags] & FlagInUse) != 0)
                _locators.WriteLive(seq, generation, seq, source, target, type);
            else
                _locators.WriteDeleted(seq, generation);
        }
    }

    private static EdgeReadHandle NotInUse(EdgeId edgeId)
        => new EdgeReadHandle(
            edgeId, inUse: false, default, default, default,
            EdgeId.Invalid, EdgeId.Invalid,
            EdgeId.Invalid, EdgeId.Invalid,
            PropertyVersionRef.Invalid);

    private static EdgeId GetFirstEdgeId(IVertexStore vertexStore, VertexId vertexId)
    {
        if (vertexStore is VersionedVertexStore ns) return ns.GetFirstEdgeId(vertexId);
        using var h = vertexStore.Read(vertexId);
        return h.FirstEdgeId;
    }

    private static void SetFirstEdgeId(IVertexStore vertexStore, VertexId vertexId, EdgeId edgeId)
    {
        if (vertexStore is VersionedVertexStore ns)
        {
            ns.UpdateFirstEdgeId(vertexId, edgeId);
            return;
        }
        var w = vertexStore.Write(vertexId);
        w.FirstEdgeId = edgeId;
        w.Dispose();
    }

    /// <summary>head version の chain prev pointer (この edge が <paramref name="side"/> 側) を in-place 更新。</summary>
    private void UpdateListPrev(EdgeId edgeId, VertexId side, EdgeId newPrev)
    {
        var ptr = _heap.GetHead(edgeId.Sequence);
        if (ptr.IsNull) return;
        var pageId = new PageId(ptr.PageId);
        using var ph = _file.PinForWrite(pageId);
        var sp = new SlottedPage(ph.Data);
        if (!sp.TryGetMutable(ptr.Slot, out var rec)) return;
        var payload = rec.Slice(HdrSize, PayloadSize);
        VertexId recSrc = new(RecordHelpers.ReadInt48(payload[OffSource..]));
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

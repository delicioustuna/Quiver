using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// Append-only bulk loader for initial data import.
/// Bypasses WAL and per-record meta flushes; does a single Commit() flush.
/// Assumes all provided IDs are fresh (no conflicts with existing records).
/// Self-loops are supported but TgtPrev/TgtNext mirror SrcPrev/SrcNext.
/// </summary>
public sealed class BulkLoader : IDisposable
{
    private readonly VersionedNodeStore _nodeStore;
    private readonly VersionedRelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    // ARCH-4 増分6: 隣接ビューは graph.quiver 内テナントへ構築する (null = 構築しない)。
    private readonly Quiver.Storage.SingleFileContainer? _container;

    private readonly List<PendingNode> _nodes = new();
    private readonly List<PendingRel> _rels = new();
    private readonly Dictionary<long, List<PendingProp>> _propsByNode = new();
    // BA-6: collects raw payload values per relationship for the V2 payload
    // lane. Keyed by (RelationshipId, PropertyKeyId) so the same loader can
    // serve multiple potential payload keys, but only the one named by
    // WithPayloadLane is actually inlined.
    private readonly Dictionary<(long RelId, int KeyId), long> _relPayloads = new();
    private PayloadLaneSpec? _payloadSpec;
    private bool _committed;

    private record struct PendingNode(long Id, int LabelId);
    private record struct PendingRel(long Id, long Src, long Tgt, int TypeId);
    private readonly record struct PendingProp(int KeyId, PropertyValueType Type, long Scalar, byte[]? Data);

    internal BulkLoader(VersionedNodeStore nodeStore, VersionedRelationshipStore relStore, PropertyStore propStore,
        Quiver.Storage.SingleFileContainer? container = null)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _container = container;
    }

    /// <summary>ノードを追加する (ラベル付き)。</summary>
    public void AppendNode(NodeId id, LabelId label)
    {
        ThrowIfCommitted();
        // ARCH-5b: 物理 slot は Sequence (利用側が gen 付き id を渡しても正しく正規化)。
        _nodes.Add(new PendingNode(id.Sequence, label.Value));
    }

    /// <summary>リレーションシップを追加する (順不同で可)。</summary>
    public void AppendRelationship(RelationshipId id, NodeId from, NodeId to, RelationshipTypeId type)
    {
        ThrowIfCommitted();
        _rels.Add(new PendingRel(id.Sequence, from.Sequence, to.Sequence, type.Value));
    }

    /// <summary>ノードにプロパティを追加する。</summary>
    public void AppendProperty(NodeId nodeId, PropertyKeyId key, in PropertyValue value)
    {
        ThrowIfCommitted();
        byte[]? data = null;
        if (value.Type is PropertyValueType.String)
            data = value.Utf8StringValue.ToArray();
        else if (value.Type is PropertyValueType.Bytes)
            data = value.BytesValue.ToArray();

        if (!_propsByNode.TryGetValue(nodeId.Sequence, out var props))
            _propsByNode[nodeId.Sequence] = props = new();
        props.Add(new PendingProp(key.Value, value.Type, value.Int64Value, data));
    }

    /// <summary>
    /// inline payload lane を設定し、<see cref="Commit"/> で指定したリレーションシッププロパティを
    /// エッジエントリ毎に inline 格納した <c>AdjacencyBlockStoreV2</c> を構築させる
    /// 。以後の <see cref="AppendRelationshipPayload"/> で lane を埋め、
    /// 値の無いエッジには <c>spec.DefaultRaw</c> が入る。
    /// </summary>
    public void WithPayloadLane(PayloadLaneSpec spec)
    {
        ThrowIfCommitted();
        if (spec.Kind == PayloadKind.None)
            throw new ArgumentException("PayloadLaneSpec.Kind must be Int64 or Double.", nameof(spec));
        _payloadSpec = spec;
    }

    /// <summary>
    /// リレーションシップの inline payload 値を記録する。<see cref="WithPayloadLane"/> の spec と
    /// キーが一致する値のみが V2 ビューに inline され、他キーは破棄される。生の long は lane の
    /// 種別に応じて Int64 値または <c>BitConverter.DoubleToInt64Bits(d)</c>。
    /// </summary>
    public void AppendRelationshipPayload(RelationshipId relId, PropertyKeyId key, long rawValue)
    {
        ThrowIfCommitted();
        _relPayloads[(relId.Sequence, key.Value)] = rawValue;
    }

    /// <summary>溜めたノード / リレーションシップ / プロパティを 1 回のフラッシュで書き出し確定する。</summary>
    public void Commit()
    {
        ThrowIfCommitted();
        _committed = true;

        CommitNodes();
        CommitRelationships();
        CommitProperties();
        if (_container != null)
            BuildAdjacencyIndex(_container);
    }

    /// <summary>ローダを破棄する (現状は no-op)。</summary>
    public void Dispose() { }

    // -----------------------------------------------------------------------

    private void CommitNodes()
    {
        long hwm = 0;
        foreach (var node in _nodes)
        {
            _nodeStore.BulkWrite(node.Id, node.LabelId);
            if (node.Id >= hwm) hwm = node.Id + 1;
        }
        _nodeStore.BulkSetHeaders(hwm, _nodes.Count);
    }

    // PW-9: single-pass dense-array pointer computation. Replaces the prior two-pass
    // Dictionary<long, List<(long, bool)>> + Dictionary<long, (long, long, long, long)>
    // approach (which allocated ~700+ MB at 10M edges). Algorithm:
    //
    //   For each rel r in RelId ascending order, the chain at every touched node
    //   interleaves rels where the node is src and rels where it is tgt. We track
    //   the most recently seen rel per node and which side it sat on; when a new
    //   rel touches the same node we (1) point its Next field at the prior tail
    //   and (2) patch the prior tail's Prev field on the appropriate side.
    //   FirstRelId for each node = the final lastByNode[node] (highest RelId).
    //
    // Self-loops: only the src side is recorded in the chain. At write time the
    // tgt-side fields mirror src — matches the prior implementation's semantics.
    private void CommitRelationships()
    {
        if (_rels.Count == 0)
        {
            _relStore.BulkSetHeaders(0, 0);
            return;
        }

        long relHwm = 0, nodeHwm = 0;
        foreach (var r in _rels)
        {
            if (r.Id  >= relHwm)  relHwm  = r.Id  + 1;
            if (r.Src >= nodeHwm) nodeHwm = r.Src + 1;
            if (r.Tgt >= nodeHwm) nodeHwm = r.Tgt + 1;
        }

        // Rels must be processed in RelId ascending order so chain pointers are
        // patched in the right direction. Callers typically append sequentially,
        // making this sort a near-noop, but we sort defensively.
        _rels.Sort((a, b) => a.Id.CompareTo(b.Id));

        var srcPrev = new long[relHwm];
        var srcNext = new long[relHwm];
        var tgtPrev = new long[relHwm];
        var tgtNext = new long[relHwm];
        Array.Fill(srcPrev, -1L);
        Array.Fill(srcNext, -1L);
        Array.Fill(tgtPrev, -1L);
        Array.Fill(tgtNext, -1L);

        var lastByNode = new long[nodeHwm];
        var lastSide   = new byte[nodeHwm]; // 0 = src at this node, 1 = tgt
        Array.Fill(lastByNode, -1L);

        foreach (var r in _rels)
        {
            long prev = lastByNode[r.Src];
            if (prev >= 0)
            {
                if (lastSide[r.Src] == 0) srcPrev[prev] = r.Id;
                else                      tgtPrev[prev] = r.Id;
            }
            srcNext[r.Id] = prev;
            lastByNode[r.Src] = r.Id;
            lastSide[r.Src] = 0;

            if (r.Tgt != r.Src)
            {
                long prevT = lastByNode[r.Tgt];
                if (prevT >= 0)
                {
                    if (lastSide[r.Tgt] == 0) srcPrev[prevT] = r.Id;
                    else                      tgtPrev[prevT] = r.Id;
                }
                tgtNext[r.Id] = prevT;
                lastByNode[r.Tgt] = r.Id;
                lastSide[r.Tgt] = 1;
            }
        }

        long hwm = 0;
        foreach (var r in _rels)
        {
            long sp = srcPrev[r.Id], sn = srcNext[r.Id];
            long tp, tn;
            if (r.Src == r.Tgt) { tp = sp; tn = sn; }
            else                { tp = tgtPrev[r.Id]; tn = tgtNext[r.Id]; }

            _relStore.BulkWrite(r.Id, r.Src, r.Tgt, r.TypeId, sp, sn, tp, tn);
            if (r.Id >= hwm) hwm = r.Id + 1;
        }
        _relStore.BulkSetHeaders(hwm, _rels.Count);

        for (long n = 0; n < nodeHwm; n++)
        {
            long head = lastByNode[n];
            if (head >= 0)
                _nodeStore.UpdateFirstRelId(new NodeId(n), new RelationshipId(head));
        }
    }

    private void CommitProperties()
    {
        foreach (var (nodeId, props) in _propsByNode)
        {
            // Build chain tail-to-head; the last written property becomes the head.
            long nextPropId = -1L;
            foreach (var prop in props)
            {
                var propId = _propStore.BulkCreate(prop.KeyId, prop.Type, prop.Scalar, prop.Data, nextPropId);
                nextPropId = propId.Sequence; // ARCH-5b: Int48 NextPropId は Sequence
            }
            _nodeStore.BulkUpdateFirstProp(nodeId, nextPropId);
        }
        _propStore.BulkFlushMeta();
    }

    private void BuildAdjacencyIndex(Quiver.Storage.SingleFileContainer container)
    {
        long nodeHwm = _nodes.Count > 0 ? _nodes.Max(n => n.Id) + 1 : 0L;
        long relHwm = _rels.Count > 0 ? _rels.Max(r => r.Id) + 1 : 0L;
        var relData = _rels.Select(r => (r.Id, r.Src, r.Tgt, r.TypeId)).ToList();

        Dictionary<long, long>? weights = null;
        if (_payloadSpec is { } spec)
        {
            // BA-6: V2 build. Filter payloads to the configured key; raw values pass
            // through (double<->long reinterpretation is the caller's responsibility
            // via AppendRelationshipPayload).
            weights = new Dictionary<long, long>(_relPayloads.Count);
            foreach (var ((relId, keyId), raw) in _relPayloads)
            {
                if (keyId == spec.PropertyKeyId)
                    weights[relId] = raw;
            }
        }

        // PW-14: relHwm を base watermark として記録し、post-bulk-load の delta
        // (id >= relHwm) を読み取り時に base ビューへ二重計上せず merge できるようにする。
        AdjacencyContainer.Build(container, relData, nodeHwm, relHwm, _payloadSpec, weights);
    }

    private void ThrowIfCommitted()
    {
        if (_committed) throw new InvalidOperationException("BulkLoader has already been committed.");
    }

}

using Quiver.Core;

namespace Quiver.Stores;

/// <summary>
/// Append-only bulk loader for initial data import.
/// Bypasses WAL and per-record meta flushes; does a single Commit() flush.
/// Assumes all provided IDs are fresh (no conflicts with existing records).
/// Self-loops are supported but TgtPrev/TgtNext mirror SrcPrev/SrcNext.
/// </summary>
public sealed class BulkLoader : IDisposable
{
    private readonly NodeStore _nodeStore;
    private readonly RelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    private readonly string? _directoryPath;

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

    internal BulkLoader(NodeStore nodeStore, RelationshipStore relStore, PropertyStore propStore,
        string? directoryPath = null)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _directoryPath = directoryPath;
    }

    public void AppendNode(NodeId id, LabelId label)
    {
        ThrowIfCommitted();
        _nodes.Add(new PendingNode(id.Value, label.Value));
    }

    public void AppendRelationship(RelationshipId id, NodeId from, NodeId to, RelationshipTypeId type)
    {
        ThrowIfCommitted();
        _rels.Add(new PendingRel(id.Value, from.Value, to.Value, type.Value));
    }

    public void AppendProperty(NodeId nodeId, PropertyKeyId key, in PropertyValue value)
    {
        ThrowIfCommitted();
        byte[]? data = null;
        if (value.Type is PropertyValueType.String)
            data = value.Utf8StringValue.ToArray();
        else if (value.Type is PropertyValueType.Bytes)
            data = value.BytesValue.ToArray();

        if (!_propsByNode.TryGetValue(nodeId.Value, out var props))
            _propsByNode[nodeId.Value] = props = new();
        props.Add(new PendingProp(key.Value, value.Type, value.Int64Value, data));
    }

    /// <summary>
    /// BA-6 / codex_advice_3 §7.2. Configure an inline payload lane so
    /// <see cref="Commit"/> builds <c>AdjacencyBlockStoreV2</c> with the named
    /// relationship property inlined per edge entry. Subsequent calls to
    /// <see cref="AppendRelationshipPayload"/> populate the lane; edges
    /// without a value receive <c>spec.DefaultRaw</c>.
    /// </summary>
    public void WithPayloadLane(PayloadLaneSpec spec)
    {
        ThrowIfCommitted();
        if (spec.Kind == PayloadKind.None)
            throw new ArgumentException("PayloadLaneSpec.Kind must be Int64 or Double.", nameof(spec));
        _payloadSpec = spec;
    }

    /// <summary>
    /// BA-6: record an inline payload value for a relationship. Only the
    /// values whose key matches <see cref="WithPayloadLane"/>'s spec are
    /// inlined into the V2 view; other keys are dropped. The raw long is
    /// either an Int64 value or <c>BitConverter.DoubleToInt64Bits(d)</c>
    /// depending on the lane kind.
    /// </summary>
    public void AppendRelationshipPayload(RelationshipId relId, PropertyKeyId key, long rawValue)
    {
        ThrowIfCommitted();
        _relPayloads[(relId.Value, key.Value)] = rawValue;
    }

    public void Commit()
    {
        ThrowIfCommitted();
        _committed = true;

        CommitNodes();
        CommitRelationships();
        CommitProperties();
        if (_directoryPath != null)
            BuildAdjacencyIndex(_directoryPath);
    }

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
                nextPropId = propId.Value;
            }
            _nodeStore.BulkUpdateFirstProp(nodeId, nextPropId);
        }
        _propStore.BulkFlushMeta();
    }

    private void BuildAdjacencyIndex(string directory)
    {
        long nodeHwm = _nodes.Count > 0 ? _nodes.Max(n => n.Id) + 1 : 0L;
        long relHwm = _rels.Count > 0 ? _rels.Max(r => r.Id) + 1 : 0L;
        var relData = _rels.Select(r => (r.Id, r.Src, r.Tgt, r.TypeId)).ToList();

        if (_payloadSpec is { } spec)
        {
            // BA-6: V2 build. Filter payloads to the configured key; raw
            // values are passed through (no double<->long reinterpretation
            // here — that is the caller's responsibility via AppendRelationshipPayload).
            var weights = new Dictionary<long, long>(_relPayloads.Count);
            foreach (var ((relId, keyId), raw) in _relPayloads)
            {
                if (keyId == spec.PropertyKeyId)
                    weights[relId] = raw;
            }
            AdjacencyBlockStoreV2.Build(
                Path.Combine(directory, "adj_v2.db"),
                Path.Combine(directory, "adj_v2_idx.dat"),
                Path.Combine(directory, "adj_v2.meta"),
                relData,
                weights,
                nodeHwm,
                spec);
        }
        else
        {
            AdjacencyBlockStore.Build(
                Path.Combine(directory, "adj.db"),
                Path.Combine(directory, "adj_idx.dat"),
                relData,
                nodeHwm);
        }

        // PW-14: record the base relationship hwm so post-bulk-load deltas
        // (rels with id >= relHwm) can be merged at read time without being
        // double-counted against the immutable base view.
        AdjacencyEpoch.CreateNew(Path.Combine(directory, "adj.epoch"), relHwm);
    }

    private void ThrowIfCommitted()
    {
        if (_committed) throw new InvalidOperationException("BulkLoader has already been committed.");
    }

}

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
        var nodeRelsList = BuildNodeRelsList();
        var ptrs = BuildRelPointers(nodeRelsList);
        CommitRelationships(ptrs, nodeRelsList);
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

    private Dictionary<long, List<(long RelId, bool IsSource)>> BuildNodeRelsList()
    {
        var result = new Dictionary<long, List<(long RelId, bool IsSource)>>();
        foreach (var rel in _rels)
        {
            GetList(result, rel.Src).Add((rel.Id, true));
            if (rel.Tgt != rel.Src)
                GetList(result, rel.Tgt).Add((rel.Id, false));
        }
        foreach (var list in result.Values)
            list.Sort((a, b) => a.RelId.CompareTo(b.RelId));
        return result;
    }

    // Builds SrcPrev/SrcNext/TgtPrev/TgtNext for every relationship.
    // The list per node is sorted ascending by RelId; head = last element (highest = newest).
    // Within the sorted list:
    //   next (older)  = element at index - 1  (SrcNext / TgtNext in traversal)
    //   prev (newer)  = element at index + 1  (SrcPrev / TgtPrev for doubly-linked deletion)
    private static Dictionary<long, (long SP, long SN, long TP, long TN)>
        BuildRelPointers(Dictionary<long, List<(long RelId, bool IsSource)>> nodeRelsList)
    {
        var ptrs = new Dictionary<long, (long, long, long, long)>();

        foreach (var (_, relList) in nodeRelsList)
        {
            for (int i = 0; i < relList.Count; i++)
            {
                var (relId, isSource) = relList[i];
                long prevId = i < relList.Count - 1 ? relList[i + 1].RelId : -1L; // newer
                long nextId = i > 0 ? relList[i - 1].RelId : -1L;                 // older

                if (!ptrs.TryGetValue(relId, out var p))
                    p = (-1L, -1L, -1L, -1L);

                ptrs[relId] = isSource
                    ? (prevId, nextId, p.Item3, p.Item4)
                    : (p.Item1, p.Item2, prevId, nextId);
            }
        }

        return ptrs;
    }

    private void CommitRelationships(
        Dictionary<long, (long SP, long SN, long TP, long TN)> ptrs,
        Dictionary<long, List<(long RelId, bool IsSource)>> nodeRelsList)
    {
        long hwm = 0;
        foreach (var rel in _rels)
        {
            ptrs.TryGetValue(rel.Id, out var p);
            long sp = p.SP, sn = p.SN, tp = p.TP, tn = p.TN;
            if (rel.Src == rel.Tgt)
                (tp, tn) = (sp, sn); // self-loop: tgt chain mirrors src chain

            _relStore.BulkWrite(rel.Id, rel.Src, rel.Tgt, rel.TypeId, sp, sn, tp, tn);
            if (rel.Id >= hwm) hwm = rel.Id + 1;
        }
        _relStore.BulkSetHeaders(hwm, _rels.Count);

        // Set each node's FirstRelId to the head of its chain (highest RelId = newest)
        foreach (var (nodeId, relList) in nodeRelsList)
        {
            long firstRelId = relList[relList.Count - 1].RelId;
            _nodeStore.UpdateFirstRelId(new NodeId(nodeId), new RelationshipId(firstRelId));
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

    private static List<(long RelId, bool IsSource)> GetList(
        Dictionary<long, List<(long RelId, bool IsSource)>> dict, long key)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = new();
        return list;
    }
}

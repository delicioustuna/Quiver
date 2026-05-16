using System.Buffers.Binary;
using Quiver.Core;

namespace Quiver.Stores;

/// <summary>
/// PW-9: streaming variant of <see cref="BulkLoader"/> intended for 10M+ edge imports.
/// Relationship records are streamed to a temporary binary file during <c>AppendRelationship</c>
/// instead of being held in a <c>List&lt;PendingRel&gt;</c>. <see cref="Commit"/> reads the
/// temp file twice — first to compute the doubly-linked chain pointers into dense
/// <c>long[]</c> arrays (sized by maxRelId+1 / maxNodeId+1), then to issue
/// <c>RelationshipStore.BulkWrite</c> in RelId-ascending order.
///
/// Precondition: <see cref="AppendRelationship"/> must be called in strictly increasing
/// <see cref="RelationshipId"/> order. This holds for typical bulk-import flows that
/// assign sequential IDs and lets the pointer pass skip a separate sort step.
///
/// Memory footprint at 10M edges (no adj build):
///   - temp file on disk:                                280 MB (28 B/rel)
///   - pointer arrays (4 × long[maxRelId+1]):           ~320 MB
///   - lastByNode / lastSide (long[] + byte[] per node): ~9 MB at 1M nodes
///   Total heap: ~330 MB, vs. ~1.7 GB with the original in-memory <c>BulkLoader</c>.
///
/// Nodes and properties remain in heap (small for typical workloads). Adjacency index
/// build re-reads the temp file into a compact list once — that is the only path that
/// still scales linearly with edge count in heap. Callers planning 10M+ imports should
/// prefer <c>buildAdjacencyIndex: false</c> and run <c>GraphDatabase.CompactAdjacency()</c>
/// later if an adjacency index is required.
/// </summary>
public sealed class StreamingBulkLoader : IDisposable
{
    private readonly NodeStore _nodeStore;
    private readonly RelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    private readonly string? _directoryPath;

    private const int RelRecordSize = 28; // Id(8) + Src(8) + Tgt(8) + TypeId(4)

    private readonly string _tempPath;
    private readonly FileStream _relTemp;
    private readonly byte[] _writeBuf = new byte[RelRecordSize];

    private long _relCount;
    private long _maxRelId = -1;
    private long _maxNodeId = -1;

    private readonly List<PendingNode> _nodes = new();
    private readonly Dictionary<long, List<PendingProp>> _propsByNode = new();
    private readonly Dictionary<(long RelId, int KeyId), long> _relPayloads = new();
    private PayloadLaneSpec? _payloadSpec;
    private bool _committed;
    private bool _disposed;

    private record struct PendingNode(long Id, int LabelId);
    private readonly record struct PendingProp(int KeyId, PropertyValueType Type, long Scalar, byte[]? Data);

    internal StreamingBulkLoader(
        NodeStore nodeStore, RelationshipStore relStore, PropertyStore propStore,
        string? directoryPath = null)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _directoryPath = directoryPath;

        _tempPath = Path.Combine(
            Path.GetTempPath(),
            "quiver_streaming_bulk_" + Guid.NewGuid().ToString("N")[..16] + ".rels");
        _relTemp = new FileStream(
            _tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.DeleteOnClose);
    }

    public void AppendNode(NodeId id, LabelId label)
    {
        ThrowIfCommitted();
        _nodes.Add(new PendingNode(id.Value, label.Value));
        if (id.Value > _maxNodeId) _maxNodeId = id.Value;
    }

    public void AppendRelationship(RelationshipId id, NodeId from, NodeId to, RelationshipTypeId type)
    {
        ThrowIfCommitted();
        if (id.Value <= _maxRelId)
            throw new InvalidOperationException(
                $"StreamingBulkLoader requires AppendRelationship in strictly increasing " +
                $"RelationshipId order (got {id.Value}, last was {_maxRelId}). " +
                $"Use BulkLoader for unordered input.");

        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(0),  id.Value);
        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(8),  from.Value);
        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(16), to.Value);
        BinaryPrimitives.WriteInt32LittleEndian(_writeBuf.AsSpan(24), type.Value);
        _relTemp.Write(_writeBuf, 0, RelRecordSize);

        _relCount++;
        _maxRelId = id.Value;
        if (from.Value > _maxNodeId) _maxNodeId = from.Value;
        if (to.Value   > _maxNodeId) _maxNodeId = to.Value;
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

    public void WithPayloadLane(PayloadLaneSpec spec)
    {
        ThrowIfCommitted();
        if (spec.Kind == PayloadKind.None)
            throw new ArgumentException("PayloadLaneSpec.Kind must be Int64 or Double.", nameof(spec));
        _payloadSpec = spec;
    }

    public void AppendRelationshipPayload(RelationshipId relId, PropertyKeyId key, long rawValue)
    {
        ThrowIfCommitted();
        _relPayloads[(relId.Value, key.Value)] = rawValue;
    }

    public void Commit()
    {
        ThrowIfCommitted();
        _committed = true;

        _relTemp.Flush();

        CommitNodes();
        CommitRelationshipsStreaming();
        CommitProperties();
        if (_directoryPath != null)
            BuildAdjacencyIndexStreaming(_directoryPath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _relTemp.Dispose(); } // FileOptions.DeleteOnClose handles cleanup
        catch { /* swallow — temp file cleanup is best-effort */ }
    }

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

    private void CommitRelationshipsStreaming()
    {
        if (_relCount == 0)
        {
            _relStore.BulkSetHeaders(0, 0);
            return;
        }

        long relHwm = _maxRelId + 1;
        long nodeHwm = _maxNodeId + 1;

        var srcPrev = new long[relHwm]; Array.Fill(srcPrev, -1L);
        var srcNext = new long[relHwm]; Array.Fill(srcNext, -1L);
        var tgtPrev = new long[relHwm]; Array.Fill(tgtPrev, -1L);
        var tgtNext = new long[relHwm]; Array.Fill(tgtNext, -1L);

        var lastByNode = new long[nodeHwm]; Array.Fill(lastByNode, -1L);
        var lastSide   = new byte[nodeHwm]; // 0 = was src at this node, 1 = was tgt

        // Pass 1: compute pointers via single forward scan. Algorithm matches the in-memory
        // BulkLoader (see BulkLoader.CommitRelationships) — chain at every node interleaves
        // rels regardless of side; the field that gets the pointer depends on which side
        // the rel sits on for that particular node.
        var buf = new byte[RelRecordSize];
        _relTemp.Position = 0;
        for (long i = 0; i < _relCount; i++)
        {
            _relTemp.ReadExactly(buf, 0, RelRecordSize);
            long id  = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));

            long prev = lastByNode[src];
            if (prev >= 0)
            {
                if (lastSide[src] == 0) srcPrev[prev] = id;
                else                    tgtPrev[prev] = id;
            }
            srcNext[id] = prev;
            lastByNode[src] = id;
            lastSide[src] = 0;

            if (tgt != src)
            {
                long prevT = lastByNode[tgt];
                if (prevT >= 0)
                {
                    if (lastSide[tgt] == 0) srcPrev[prevT] = id;
                    else                    tgtPrev[prevT] = id;
                }
                tgtNext[id] = prevT;
                lastByNode[tgt] = id;
                lastSide[tgt] = 1;
            }
        }

        // Pass 2: stream rels again, applying pointers via dense lookup.
        long hwm = 0;
        _relTemp.Position = 0;
        for (long i = 0; i < _relCount; i++)
        {
            _relTemp.ReadExactly(buf, 0, RelRecordSize);
            long id     = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));
            int  typeId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24));

            long sp = srcPrev[id], sn = srcNext[id];
            long tp, tn;
            if (src == tgt) { tp = sp; tn = sn; }
            else            { tp = tgtPrev[id]; tn = tgtNext[id]; }

            _relStore.BulkWrite(id, src, tgt, typeId, sp, sn, tp, tn);
            if (id >= hwm) hwm = id + 1;
        }
        _relStore.BulkSetHeaders(hwm, _relCount);

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

    private void BuildAdjacencyIndexStreaming(string directory)
    {
        // Materialize rels into a compact list for the existing AdjacencyBlockStore.Build.
        // Full streaming adj-build (chunk-sort by src/tgt) is a future task — see PW-9 notes.
        var relData = new List<(long Id, long Src, long Tgt, int TypeId)>((int)Math.Min(_relCount, int.MaxValue));
        var buf = new byte[RelRecordSize];
        _relTemp.Position = 0;
        for (long i = 0; i < _relCount; i++)
        {
            _relTemp.ReadExactly(buf, 0, RelRecordSize);
            long id     = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));
            int  typeId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24));
            relData.Add((id, src, tgt, typeId));
        }

        long nodeHwm = _maxNodeId + 1;
        long relHwm = _maxRelId + 1;

        if (_payloadSpec is { } spec)
        {
            var weights = new Dictionary<long, long>(_relPayloads.Count);
            foreach (var ((relId, keyId), raw) in _relPayloads)
                if (keyId == spec.PropertyKeyId)
                    weights[relId] = raw;
            AdjacencyBlockStoreV2.Build(
                Path.Combine(directory, "adj_v2.db"),
                Path.Combine(directory, "adj_v2_idx.dat"),
                Path.Combine(directory, "adj_v2.meta"),
                relData, weights, nodeHwm, spec);
        }
        else
        {
            AdjacencyBlockStore.Build(
                Path.Combine(directory, "adj.db"),
                Path.Combine(directory, "adj_idx.dat"),
                relData, nodeHwm);
        }
        AdjacencyEpoch.CreateNew(Path.Combine(directory, "adj.epoch"), relHwm);
    }

    private void ThrowIfCommitted()
    {
        if (_committed)
            throw new InvalidOperationException("StreamingBulkLoader has already been committed.");
    }
}

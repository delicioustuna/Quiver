using Quiver.Core;

namespace Quiver;

/// <summary>
/// PW-16 / codex_advice_3 §7.5. Two-tier per-node degree lookup that backs
/// <see cref="GraphStats"/>. When the observed <c>NodeId</c> space is dense
/// (<c>maxNodeId / nodeCount &lt;= DenseThreshold</c>) the lookup uses
/// direct arrays indexed by <c>NodeId.Value</c> plus a single bit per node
/// for power-node flagging — <c>O(1)</c> with no boxing and no dictionary
/// chain walk. When the id space is sparse the lookup falls back to a
/// per-node dictionary that only materialises power-node entries (matching
/// the pre-PW-16 memory footprint).
/// </summary>
public sealed class NodeDegreeLookup
{
    /// <summary>
    /// Maximum permitted ratio of <c>(maxNodeId + 1) / nodeCount</c> for the
    /// dense path. With the default of <c>4.0</c> a graph that has used at
    /// least 25% of its id space is dense; anything sparser falls back to a
    /// dictionary so we don't allocate hundreds of MB for a graph that only
    /// has a few thousand live nodes scattered across a huge id range.
    /// </summary>
    public const double DefaultDenseThreshold = 4.0;

    public static readonly NodeDegreeLookup Empty = new(
        dense: false,
        outDegrees: null,
        inDegrees: null,
        powerNodeBits: null,
        denseLength: 0,
        sparseDegrees: null,
        sparsePowerNodes: new Dictionary<NodeId, NodeDegreeSummary>(),
        powerNodeThreshold: GraphStats.PowerNodeDegreeThreshold,
        maxNodeIdObserved: -1,
        denseThreshold: DefaultDenseThreshold);

    private readonly long[]? _outDegrees;
    private readonly long[]? _inDegrees;
    private readonly ulong[]? _powerNodeBits;
    private readonly int _denseLength;
    private readonly IReadOnlyDictionary<NodeId, NodeDegreeSummary>? _sparseDegrees;
    private readonly IReadOnlyDictionary<NodeId, NodeDegreeSummary> _sparsePowerNodes;

    public bool IsDense { get; }
    public int DenseLength => _denseLength;
    public long PowerNodeThreshold { get; }
    public long MaxNodeIdObserved { get; }
    public double DenseThreshold { get; }
    public int PowerNodeCount { get; }

    private NodeDegreeLookup(
        bool dense,
        long[]? outDegrees,
        long[]? inDegrees,
        ulong[]? powerNodeBits,
        int denseLength,
        IReadOnlyDictionary<NodeId, NodeDegreeSummary>? sparseDegrees,
        IReadOnlyDictionary<NodeId, NodeDegreeSummary> sparsePowerNodes,
        long powerNodeThreshold,
        long maxNodeIdObserved,
        double denseThreshold)
    {
        IsDense = dense;
        _outDegrees = outDegrees;
        _inDegrees = inDegrees;
        _powerNodeBits = powerNodeBits;
        _denseLength = denseLength;
        _sparseDegrees = sparseDegrees;
        _sparsePowerNodes = sparsePowerNodes;
        PowerNodeThreshold = powerNodeThreshold;
        MaxNodeIdObserved = maxNodeIdObserved;
        DenseThreshold = denseThreshold;
        PowerNodeCount = sparsePowerNodes.Count;
    }

    /// <summary>
    /// Returns the recorded out/in degrees for <paramref name="nodeId"/> if
    /// the lookup is dense and <paramref name="nodeId"/> is within range, or
    /// when the sparse fallback happens to track the node (only power nodes
    /// are tracked sparsely). Returns <c>false</c> otherwise — callers must
    /// not interpret a <c>false</c> return as "degree is zero".
    /// </summary>
    public bool TryGetDegree(NodeId nodeId, out long outDegree, out long inDegree)
    {
        long v = nodeId.Value;
        if (IsDense)
        {
            if ((ulong)v < (ulong)_denseLength)
            {
                outDegree = _outDegrees![v];
                inDegree = _inDegrees![v];
                return true;
            }
            outDegree = 0;
            inDegree = 0;
            return false;
        }

        if (_sparseDegrees is not null && _sparseDegrees.TryGetValue(nodeId, out var s))
        {
            outDegree = s.OutDegree;
            inDegree = s.InDegree;
            return true;
        }
        outDegree = 0;
        inDegree = 0;
        return false;
    }

    /// <summary>
    /// O(1) power-node check. In dense mode this reads a single bit; in
    /// sparse mode it falls back to a dictionary <c>ContainsKey</c>.
    /// </summary>
    public bool IsLikelyPowerNode(NodeId nodeId)
    {
        long v = nodeId.Value;
        if (IsDense)
        {
            if ((ulong)v >= (ulong)_denseLength) return false;
            int word = (int)(v >> 6);
            int bit = (int)(v & 63);
            return (_powerNodeBits![word] & (1UL << bit)) != 0;
        }
        return _sparsePowerNodes.ContainsKey(nodeId);
    }

    /// <summary>
    /// Enumerate every power node. Dense mode walks the bitset; sparse mode
    /// enumerates the underlying dictionary. The enumeration is order-stable
    /// in dense mode (ascending <c>NodeId.Value</c>) and unordered in sparse
    /// mode.
    /// </summary>
    public IEnumerable<NodeDegreeSummary> EnumeratePowerNodes()
    {
        if (IsDense)
        {
            int len = _denseLength;
            for (int word = 0; word < _powerNodeBits!.Length; word++)
            {
                ulong bits = _powerNodeBits[word];
                while (bits != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    long v = ((long)word << 6) | (uint)bit;
                    if (v >= len) yield break;
                    yield return new NodeDegreeSummary(new NodeId(v), _outDegrees![v], _inDegrees![v]);
                }
            }
        }
        else
        {
            foreach (var kv in _sparsePowerNodes)
                yield return kv.Value;
        }
    }

    /// <summary>
    /// Snapshot the power-node set as a dictionary. This exists so legacy
    /// callers of <see cref="GraphStats.PowerNodes"/> keep working without
    /// forcing dense-mode collection to materialise the dictionary up-front.
    /// </summary>
    public IReadOnlyDictionary<NodeId, NodeDegreeSummary> SnapshotPowerNodes()
    {
        if (!IsDense) return _sparsePowerNodes;
        var dict = new Dictionary<NodeId, NodeDegreeSummary>(capacity: PowerNodeCount);
        foreach (var s in EnumeratePowerNodes())
            dict[s.NodeId] = s;
        return dict;
    }

    // ──────────────────────────── Builder ────────────────────────────

    internal sealed class Builder
    {
        private readonly List<NodeDegreeRecord> _records = [];
        private readonly Dictionary<NodeId, NodeDegreeSummary> _powerNodes = [];
        private readonly long _powerNodeThreshold;
        private readonly double _denseThreshold;
        private long _maxNodeId = -1;

        internal Builder(long powerNodeThreshold, double denseThreshold)
        {
            _powerNodeThreshold = powerNodeThreshold;
            _denseThreshold = denseThreshold;
        }

        internal void Record(NodeId nodeId, long outDegree, long inDegree)
        {
            _records.Add(new NodeDegreeRecord(nodeId, outDegree, inDegree));
            if (nodeId.Value > _maxNodeId) _maxNodeId = nodeId.Value;
            long total = outDegree + inDegree;
            if (total >= _powerNodeThreshold)
                _powerNodes[nodeId] = new NodeDegreeSummary(nodeId, outDegree, inDegree);
        }

        internal NodeDegreeLookup Build()
        {
            int n = _records.Count;
            if (n == 0)
            {
                return new NodeDegreeLookup(
                    dense: false,
                    outDegrees: null, inDegrees: null, powerNodeBits: null, denseLength: 0,
                    sparseDegrees: null, sparsePowerNodes: _powerNodes,
                    powerNodeThreshold: _powerNodeThreshold,
                    maxNodeIdObserved: _maxNodeId,
                    denseThreshold: _denseThreshold);
            }

            long denseCapacity = _maxNodeId + 1;
            double ratio = (double)denseCapacity / n;
            bool dense = ratio <= _denseThreshold;

            // Bound the dense allocation: above ~32M nodes the direct array
            // (16 B per node + 1 bit) is large enough that the caller probably
            // wants explicit consent. Fall back to sparse silently — caller
            // can still override via the threshold parameter on Collect.
            if (denseCapacity > int.MaxValue / 16) dense = false;

            if (dense)
            {
                int len = (int)denseCapacity;
                var outs = new long[len];
                var ins = new long[len];
                int bitWords = (len + 63) >> 6;
                var bits = new ulong[bitWords == 0 ? 1 : bitWords];

                foreach (var r in _records)
                {
                    int v = (int)r.NodeId.Value;
                    outs[v] = r.OutDegree;
                    ins[v] = r.InDegree;
                    if (r.OutDegree + r.InDegree >= _powerNodeThreshold)
                    {
                        int word = v >> 6;
                        int bit = v & 63;
                        bits[word] |= 1UL << bit;
                    }
                }

                return new NodeDegreeLookup(
                    dense: true,
                    outDegrees: outs, inDegrees: ins, powerNodeBits: bits, denseLength: len,
                    sparseDegrees: null, sparsePowerNodes: _powerNodes,
                    powerNodeThreshold: _powerNodeThreshold,
                    maxNodeIdObserved: _maxNodeId,
                    denseThreshold: _denseThreshold);
            }

            // Sparse: only materialise a per-node dictionary for power nodes,
            // matching pre-PW-16 memory behaviour.
            return new NodeDegreeLookup(
                dense: false,
                outDegrees: null, inDegrees: null, powerNodeBits: null, denseLength: 0,
                sparseDegrees: _powerNodes,
                sparsePowerNodes: _powerNodes,
                powerNodeThreshold: _powerNodeThreshold,
                maxNodeIdObserved: _maxNodeId,
                denseThreshold: _denseThreshold);
        }

        private readonly record struct NodeDegreeRecord(NodeId NodeId, long OutDegree, long InDegree);
    }
}

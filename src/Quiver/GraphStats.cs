using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

public sealed class DegreeHistogram
{
    // Bucket upper-bounds: 0, 1, 3, 7, 15, 31, 63, 127, 255, ∞
    private readonly long[] _counts = new long[10];

    public long TotalNodes { get; private set; }
    public long TotalDegree { get; private set; }
    public long MaxDegree { get; private set; }
    public double MeanDegree => TotalNodes == 0 ? 0.0 : (double)TotalDegree / TotalNodes;
    public IReadOnlyList<long> BucketCounts => _counts;

    internal void Record(long degree)
    {
        TotalNodes++;
        TotalDegree += degree;
        if (degree > MaxDegree) MaxDegree = degree;
        _counts[BucketFor(degree)]++;
    }

    private static int BucketFor(long d) => d switch
    {
        0 => 0,
        1 => 1,
        <= 3 => 2,
        <= 7 => 3,
        <= 15 => 4,
        <= 31 => 5,
        <= 63 => 6,
        <= 127 => 7,
        <= 255 => 8,
        _ => 9,
    };
}

/// <summary>
/// Per-node degree summary recorded for "power nodes" (nodes whose total degree
/// exceeds <see cref="GraphStats.PowerNodeDegreeThreshold"/>). BA-4.
/// </summary>
public readonly record struct NodeDegreeSummary(
    NodeId NodeId,
    long OutDegree,
    long InDegree)
{
    public long TotalDegree => OutDegree + InDegree;
}

/// <summary>
/// Set of <see cref="PropertyValueType"/> values observed for a property key.
/// BA-8 will replace this with a richer <c>PropertyTypeFlags</c> mask; we use
/// a thin bitmask here so the surface area is easy to migrate.
/// </summary>
[Flags]
public enum PropertyValueTypeMask : uint
{
    None   = 0,
    Bool   = 1u << 1,
    Int32  = 1u << 2,
    Int64  = 1u << 3,
    Double = 1u << 4,
    String = 1u << 5,
    Bytes  = 1u << 6,

    Numeric = Int32 | Int64 | Double,
}

/// <summary>
/// Per-property-key statistics. Populated by <see cref="GraphStats.Collect"/>.
/// </summary>
public sealed class PropertyKeyStats
{
    private readonly HashSet<long> _distinctScalars = [];
    private readonly HashSet<string> _distinctStrings = [];
    private bool _distinctSaturated;

    /// <summary>Maximum number of distinct values tracked exactly before reporting via <see cref="DistinctEstimate"/>.</summary>
    public const int DistinctTrackingCap = 4096;

    public PropertyKeyId KeyId { get; init; }

    public PropertyValueTypeMask ObservedTypes { get; private set; }

    /// <summary>Total number of property occurrences observed for this key (across nodes + relationships).</summary>
    public long Count { get; private set; }

    /// <summary>Number of entities (in the scanned scope) that did NOT have this key set.</summary>
    public long NullOrMissingCount { get; private set; }

    /// <summary>
    /// Approximate distinct value count. Exact while &lt;= <see cref="DistinctTrackingCap"/>;
    /// once saturated the value is clamped to the cap (HyperLogLog upgrade is left for a follow-up).
    /// </summary>
    public long DistinctEstimate { get; private set; }

    public long MinInt64 { get; private set; } = long.MaxValue;
    public long MaxInt64 { get; private set; } = long.MinValue;
    public double MinDouble { get; private set; } = double.PositiveInfinity;
    public double MaxDouble { get; private set; } = double.NegativeInfinity;

    public bool HasNumericRange =>
        (ObservedTypes & PropertyValueTypeMask.Numeric) != 0 && MinInt64 != long.MaxValue;

    public bool HasDoubleRange =>
        (ObservedTypes & PropertyValueTypeMask.Double) != 0 && !double.IsPositiveInfinity(MinDouble);

    internal void SetNullOrMissingCount(long n) => NullOrMissingCount = n;

    internal void Observe(in PropertyValue value)
    {
        Count++;
        switch (value.Type)
        {
            case PropertyValueType.Bool:
                ObservedTypes |= PropertyValueTypeMask.Bool;
                BumpDistinctScalar(value.BoolValue ? 1L : 0L);
                break;
            case PropertyValueType.Int32:
                ObservedTypes |= PropertyValueTypeMask.Int32;
                long i32 = value.Int32Value;
                if (i32 < MinInt64) MinInt64 = i32;
                if (i32 > MaxInt64) MaxInt64 = i32;
                BumpDistinctScalar(i32);
                break;
            case PropertyValueType.Int64:
                ObservedTypes |= PropertyValueTypeMask.Int64;
                long i64 = value.Int64Value;
                if (i64 < MinInt64) MinInt64 = i64;
                if (i64 > MaxInt64) MaxInt64 = i64;
                BumpDistinctScalar(i64);
                break;
            case PropertyValueType.Double:
                ObservedTypes |= PropertyValueTypeMask.Double;
                double d = value.DoubleValue;
                if (d < MinDouble) MinDouble = d;
                if (d > MaxDouble) MaxDouble = d;
                BumpDistinctScalar(BitConverter.DoubleToInt64Bits(d));
                break;
            case PropertyValueType.String:
                ObservedTypes |= PropertyValueTypeMask.String;
                BumpDistinctString(System.Text.Encoding.UTF8.GetString(value.Utf8StringValue));
                break;
            case PropertyValueType.Bytes:
                ObservedTypes |= PropertyValueTypeMask.Bytes;
                // Bytes distinct tracking is intentionally skipped to bound memory.
                if (!_distinctSaturated && DistinctEstimate < DistinctTrackingCap)
                    DistinctEstimate++;
                break;
        }
    }

    private void BumpDistinctScalar(long bits)
    {
        if (_distinctSaturated) return;
        if (_distinctScalars.Add(bits))
        {
            DistinctEstimate = _distinctScalars.Count + _distinctStrings.Count;
            if (DistinctEstimate >= DistinctTrackingCap) _distinctSaturated = true;
        }
    }

    private void BumpDistinctString(string s)
    {
        if (_distinctSaturated) return;
        if (_distinctStrings.Add(s))
        {
            DistinctEstimate = _distinctScalars.Count + _distinctStrings.Count;
            if (DistinctEstimate >= DistinctTrackingCap) _distinctSaturated = true;
        }
    }
}

public sealed class GraphStats
{
    /// <summary>Default node-degree threshold above which a node is recorded in <see cref="PowerNodes"/>.</summary>
    public const int PowerNodeDegreeThreshold = 256;

    public static readonly GraphStats Empty = new();

    public IReadOnlyDictionary<LabelId, long> LabelCardinality { get; private init; }
        = new Dictionary<LabelId, long>();
    public IReadOnlyDictionary<RelationshipTypeId, long> EdgeTypeFrequency { get; private init; }
        = new Dictionary<RelationshipTypeId, long>();

    public DegreeHistogram GlobalDegreeHistogram { get; private init; } = new();
    public DegreeHistogram GlobalOutDegree { get; private init; } = new();
    public DegreeHistogram GlobalInDegree { get; private init; } = new();

    public IReadOnlyDictionary<LabelId, DegreeHistogram> DegreeByLabel { get; private init; }
        = new Dictionary<LabelId, DegreeHistogram>();
    public IReadOnlyDictionary<RelationshipTypeId, DegreeHistogram> OutDegreeByType { get; private init; }
        = new Dictionary<RelationshipTypeId, DegreeHistogram>();
    public IReadOnlyDictionary<RelationshipTypeId, DegreeHistogram> InDegreeByType { get; private init; }
        = new Dictionary<RelationshipTypeId, DegreeHistogram>();

    public IReadOnlyDictionary<NodeId, NodeDegreeSummary> PowerNodes { get; private init; }
        = new Dictionary<NodeId, NodeDegreeSummary>();

    public IReadOnlyDictionary<PropertyKeyId, PropertyKeyStats> PropertyKeys { get; private init; }
        = new Dictionary<PropertyKeyId, PropertyKeyStats>();

    public long TotalNodes { get; private init; }
    public long TotalRelationships { get; private init; }

    public long EstimateCardinality(LabelId label)
        => LabelCardinality.TryGetValue(label, out var n) ? n : 0;

    public double EstimateSelectivity(LabelId label)
        => TotalNodes == 0 ? 0.0 : (double)EstimateCardinality(label) / TotalNodes;

    public double EstimateMeanDegree(LabelId label)
        => DegreeByLabel.TryGetValue(label, out var h) ? h.MeanDegree : GlobalDegreeHistogram.MeanDegree;

    /// <summary>
    /// Estimate per-node fan-out for a one-hop expansion under the given filters.
    /// Falls back from the most specific signal (direction+type) to global mean.
    /// </summary>
    public double EstimateFanOut(LabelId? sourceLabel, RelationshipTypeId? typeFilter, Direction direction)
    {
        if (typeFilter.HasValue)
        {
            double mean = direction switch
            {
                Direction.Outgoing => OutDegreeByType.TryGetValue(typeFilter.Value, out var oh) ? oh.MeanDegree : 0.0,
                Direction.Incoming => InDegreeByType.TryGetValue(typeFilter.Value, out var ih) ? ih.MeanDegree : 0.0,
                _ =>
                    (OutDegreeByType.TryGetValue(typeFilter.Value, out var bo) ? bo.MeanDegree : 0.0) +
                    (InDegreeByType.TryGetValue(typeFilter.Value, out var bi) ? bi.MeanDegree : 0.0),
            };

            // Fallback to global edge-type frequency / TotalNodes when histograms are empty
            // (e.g. when the type appears but no node had it in the sampled scope).
            if (mean == 0.0 && TotalNodes > 0 &&
                EdgeTypeFrequency.TryGetValue(typeFilter.Value, out var edgeCount))
            {
                mean = direction == Direction.Both
                    ? 2.0 * edgeCount / TotalNodes
                    : (double)edgeCount / TotalNodes;
            }
            return mean;
        }

        // No type filter: use direction-aware global histograms when available.
        return direction switch
        {
            Direction.Outgoing => GlobalOutDegree.MeanDegree,
            Direction.Incoming => GlobalInDegree.MeanDegree,
            _ => GlobalDegreeHistogram.MeanDegree,
        };
    }

    public bool IsLikelyPowerNode(NodeId nodeId) => PowerNodes.ContainsKey(nodeId);

    public static GraphStats Collect(ITransaction tx) => Collect(tx, PowerNodeDegreeThreshold);

    public static GraphStats Collect(ITransaction tx, int powerNodeThreshold)
    {
        var labelCard       = new Dictionary<LabelId, long>();
        var edgeFreq        = new Dictionary<RelationshipTypeId, long>();
        var degreeByLabel   = new Dictionary<LabelId, DegreeHistogram>();
        var outDegreeByType = new Dictionary<RelationshipTypeId, DegreeHistogram>();
        var inDegreeByType  = new Dictionary<RelationshipTypeId, DegreeHistogram>();
        var globalHist      = new DegreeHistogram();
        var globalOut       = new DegreeHistogram();
        var globalIn        = new DegreeHistogram();
        var powerNodes      = new Dictionary<NodeId, NodeDegreeSummary>();
        var propertyKeys    = new Dictionary<PropertyKeyId, PropertyKeyStats>();

        long totalNodes = 0;
        long totalRels  = 0;

        // Per-type counters reused across nodes to avoid Dictionary allocations per node.
        var perTypeOut = new Dictionary<RelationshipTypeId, long>();
        var perTypeIn  = new Dictionary<RelationshipTypeId, long>();

        foreach (var nodeId in tx.Nodes.Scan())
        {
            var node = tx.Nodes.Read(nodeId);
            if (!node.InUse) continue;

            totalNodes++;
            var label = node.Label;

            labelCard.TryGetValue(label, out var lc);
            labelCard[label] = lc + 1;

            if (!degreeByLabel.ContainsKey(label))
                degreeByLabel[label] = new DegreeHistogram();

            perTypeOut.Clear();
            perTypeIn.Clear();
            long outDegree = 0;
            long inDegree  = 0;

            // Walk the full relationship chain (both directions).
            // For source-side visits we additionally process relationship properties so
            // every relationship is visited exactly once for property collection.
            var relId = node.FirstRelationshipId;
            while (relId.IsValid)
            {
                var rel = tx.Relationships.Read(relId);
                bool isSource = rel.Source == nodeId;
                var nextId = isSource ? rel.SourceNext : rel.TargetNext;

                if (isSource)
                {
                    totalRels++;
                    edgeFreq.TryGetValue(rel.Type, out var tc);
                    edgeFreq[rel.Type] = tc + 1;

                    outDegree++;
                    perTypeOut.TryGetValue(rel.Type, out var po);
                    perTypeOut[rel.Type] = po + 1;

                    var rpe = tx.Properties.Enumerate(rel.FirstPropertyId);
                    while (rpe.MoveNext())
                    {
                        var ph = rpe.Current;
                        if (!propertyKeys.TryGetValue(ph.KeyId, out var pks))
                            propertyKeys[ph.KeyId] = pks = new PropertyKeyStats { KeyId = ph.KeyId };
                        pks.Observe(ph.Value);
                    }
                }
                else
                {
                    inDegree++;
                    perTypeIn.TryGetValue(rel.Type, out var pi);
                    perTypeIn[rel.Type] = pi + 1;
                }

                relId = nextId;
            }

            long nodeDegree = outDegree + inDegree;
            globalHist.Record(nodeDegree);
            globalOut.Record(outDegree);
            globalIn.Record(inDegree);
            degreeByLabel[label].Record(nodeDegree);

            foreach (var typeId in perTypeOut.Keys)
            {
                if (!outDegreeByType.TryGetValue(typeId, out var h))
                    outDegreeByType[typeId] = h = new DegreeHistogram();
                h.Record(perTypeOut[typeId]);
            }
            foreach (var typeId in perTypeIn.Keys)
            {
                if (!inDegreeByType.TryGetValue(typeId, out var h))
                    inDegreeByType[typeId] = h = new DegreeHistogram();
                h.Record(perTypeIn[typeId]);
            }

            if (nodeDegree >= powerNodeThreshold)
                powerNodes[nodeId] = new NodeDegreeSummary(nodeId, outDegree, inDegree);

            // Node properties → PropertyKeyStats
            var propEnum = tx.Properties.Enumerate(node.FirstPropertyId);
            while (propEnum.MoveNext())
            {
                var ph = propEnum.Current;
                if (!propertyKeys.TryGetValue(ph.KeyId, out var pks))
                    propertyKeys[ph.KeyId] = pks = new PropertyKeyStats { KeyId = ph.KeyId };
                pks.Observe(ph.Value);
            }
        }

        // Each entity (node or relationship) holds at most one value per key, so
        // missing = totalEntities - observedCount.
        long totalEntities = totalNodes + totalRels;
        foreach (var pks in propertyKeys.Values)
        {
            long missing = totalEntities - pks.Count;
            pks.SetNullOrMissingCount(missing < 0 ? 0 : missing);
        }

        return new GraphStats
        {
            LabelCardinality      = labelCard,
            EdgeTypeFrequency     = edgeFreq,
            GlobalDegreeHistogram = globalHist,
            GlobalOutDegree       = globalOut,
            GlobalInDegree        = globalIn,
            DegreeByLabel         = degreeByLabel,
            OutDegreeByType       = outDegreeByType,
            InDegreeByType        = inDegreeByType,
            PowerNodes            = powerNodes,
            PropertyKeys          = propertyKeys,
            TotalNodes            = totalNodes,
            TotalRelationships    = totalRels,
        };
    }
}

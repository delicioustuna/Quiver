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
/// Legacy alias kept for source compatibility while BA-8 migrates callers to
/// <see cref="Quiver.Core.PropertyTypeFlags"/>. The bit values intentionally match
/// the low bits of <c>PropertyTypeFlags</c> so casts between the two are safe.
/// </summary>
[Obsolete("Use Quiver.Core.PropertyTypeFlags instead. PropertyValueTypeMask will be removed.")]
[Flags]
public enum PropertyValueTypeMask : uint
{
    None   = 0,
    Bool   = (uint)(PropertyTypeFlags.Bool),
    Int32  = (uint)(PropertyTypeFlags.Int32),
    Int64  = (uint)(PropertyTypeFlags.Int64),
    Double = (uint)(PropertyTypeFlags.Double),
    String = (uint)(PropertyTypeFlags.String),
    Bytes  = (uint)(PropertyTypeFlags.Bytes),

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

    /// <summary>
    /// このキーに対して観測された全値の <see cref="PropertyTypeFlags"/> ビット和。
    /// 数値述語 / 文字列述語が原理的にマッチし得るかをオプティマイザが判定するのに使う。
    /// </summary>
    public PropertyTypeFlags ObservedTypes { get; private set; }

    /// <summary>Legacy view of <see cref="ObservedTypes"/>. Kept while callers migrate to <see cref="PropertyTypeFlags"/>.</summary>
#pragma warning disable CS0618
    public PropertyValueTypeMask ObservedTypesLegacy => (PropertyValueTypeMask)(uint)ObservedTypes;
#pragma warning restore CS0618

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
        (ObservedTypes & PropertyTypeFlags.Numeric) != 0 && MinInt64 != long.MaxValue;

    public bool HasDoubleRange =>
        (ObservedTypes & PropertyTypeFlags.Double) != 0 && !double.IsPositiveInfinity(MinDouble);

    internal void SetNullOrMissingCount(long n) => NullOrMissingCount = n;

    internal void Observe(in PropertyValue value)
    {
        Count++;
        ObservedTypes |= value.Type.ToFlags();
        switch (value.Type)
        {
            case PropertyValueType.Bool:
                BumpDistinctScalar(value.BoolValue ? 1L : 0L);
                break;
            case PropertyValueType.Int32:
                long i32 = value.Int32Value;
                if (i32 < MinInt64) MinInt64 = i32;
                if (i32 > MaxInt64) MaxInt64 = i32;
                BumpDistinctScalar(i32);
                break;
            case PropertyValueType.Int64:
                long i64 = value.Int64Value;
                if (i64 < MinInt64) MinInt64 = i64;
                if (i64 > MaxInt64) MaxInt64 = i64;
                BumpDistinctScalar(i64);
                break;
            case PropertyValueType.Double:
                double d = value.DoubleValue;
                if (d < MinDouble) MinDouble = d;
                if (d > MaxDouble) MaxDouble = d;
                BumpDistinctScalar(BitConverter.DoubleToInt64Bits(d));
                break;
            case PropertyValueType.String:
                BumpDistinctString(System.Text.Encoding.UTF8.GetString(value.Utf8StringValue));
                break;
            case PropertyValueType.Bytes:
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

    /// <summary>
    /// PW-16: legacy dictionary view of power nodes. Backed by
    /// <see cref="NodeDegrees"/> — materialised lazily so dense-mode collections
    /// do not pay for the dictionary unless a caller asks for this view.
    /// </summary>
    public IReadOnlyDictionary<NodeId, NodeDegreeSummary> PowerNodes
        => _powerNodesCache ??= NodeDegrees.SnapshotPowerNodes();
    private IReadOnlyDictionary<NodeId, NodeDegreeSummary>? _powerNodesCache;

    /// <summary>
    /// PW-16 / codex_advice_3 §7.5. Direct-array degree cache when the
    /// observed <c>NodeId</c> space is dense, dictionary fallback when
    /// sparse. Use this in hot paths instead of <see cref="PowerNodes"/>.
    /// </summary>
    public NodeDegreeLookup NodeDegrees { get; private init; } = NodeDegreeLookup.Empty;

    public IReadOnlyDictionary<PropertyKeyId, PropertyKeyStats> PropertyKeys { get; private init; }
        = new Dictionary<PropertyKeyId, PropertyKeyStats>();

    public long TotalNodes { get; private init; }
    public long TotalRelationships { get; private init; }

    /// <summary>
    /// VEC-12: 観測対象 backend が <c>NodeByLabelScan</c> を O(|L|) で提供できるか。
    /// バイナリ backend で <c>LabelNodeIndex</c> sidecar が接続されているとき <c>true</c>。
    /// <c>InlineGraphAccessMethods</c> 経路 / ANN bypass 等 sidecar 無し backend では <c>false</c>。
    /// <see cref="Quiver.Client.Internal.PendingKnnBuilder"/> の push-down 閾値判定で、
    /// dim-aware piecewise table を引くか legacy 30% 単一閾値を引くかを切り替えるのに使う。
    /// テストから明示的に <c>false</c> 経路を再現できるよう <c>init</c> を公開している。
    /// </summary>
    public bool HasFastLabelIndex { get; init; }

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

            // ヒストグラムが空のとき (例えば該当型は存在するがサンプル範囲のノードでは観測されないとき)
            // は、グローバルなエッジ型頻度 / TotalNodes へフォールバックする。
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

    /// <summary>
    /// PW-16: O(1) power-node check via <see cref="NodeDegrees"/> (bit test in
    /// dense mode, dictionary lookup in sparse mode).
    /// </summary>
    public bool IsLikelyPowerNode(NodeId nodeId) => NodeDegrees.IsLikelyPowerNode(nodeId);

    /// <summary>
    /// PW-16: O(1) degree lookup. Returns <c>false</c> when the node id is
    /// outside the dense range or (in sparse mode) is not a tracked power
    /// node — callers must not treat <c>false</c> as "degree is zero".
    /// </summary>
    public bool TryGetDegree(NodeId nodeId, out long outDegree, out long inDegree)
        => NodeDegrees.TryGetDegree(nodeId, out outDegree, out inDegree);

    /// <summary>
    /// VEC-12: 既存 stats から <see cref="HasFastLabelIndex"/> bit のみを差し替えた複製を返す。
    /// テストやベンチで「同じ cardinality を持つ stats を sidecar 有 / 無で比較する」用途を想定。
    /// 他フィールドは参照渡しでコピーされる (元 stats は不変なので副作用は無い)。
    /// </summary>
    public GraphStats WithFastLabelIndex(bool hasFastLabelIndex)
    {
        if (hasFastLabelIndex == HasFastLabelIndex) return this;
        return new GraphStats
        {
            LabelCardinality      = LabelCardinality,
            EdgeTypeFrequency     = EdgeTypeFrequency,
            GlobalDegreeHistogram = GlobalDegreeHistogram,
            GlobalOutDegree       = GlobalOutDegree,
            GlobalInDegree        = GlobalInDegree,
            DegreeByLabel         = DegreeByLabel,
            OutDegreeByType       = OutDegreeByType,
            InDegreeByType        = InDegreeByType,
            NodeDegrees           = NodeDegrees,
            PropertyKeys          = PropertyKeys,
            TotalNodes            = TotalNodes,
            TotalRelationships    = TotalRelationships,
            HasFastLabelIndex     = hasFastLabelIndex,
        };
    }

    public static GraphStats Collect(ITransaction tx) => Collect(tx, PowerNodeDegreeThreshold);

    public static GraphStats Collect(ITransaction tx, int powerNodeThreshold)
        => Collect(tx, powerNodeThreshold, NodeDegreeLookup.DefaultDenseThreshold);

    /// <summary>
    /// PW-16 overload: lets callers tune the dense/sparse cut-over for the
    /// per-node degree lookup. The default
    /// <see cref="NodeDegreeLookup.DefaultDenseThreshold"/> (4.0) is suitable
    /// for bulk-loaded graphs; tests that intentionally exercise the sparse
    /// fallback can pass a value &lt; 1.0.
    /// </summary>
    public static GraphStats Collect(ITransaction tx, int powerNodeThreshold, double denseThreshold)
    {
        var labelCard       = new Dictionary<LabelId, long>();
        var edgeFreq        = new Dictionary<RelationshipTypeId, long>();
        var degreeByLabel   = new Dictionary<LabelId, DegreeHistogram>();
        var outDegreeByType = new Dictionary<RelationshipTypeId, DegreeHistogram>();
        var inDegreeByType  = new Dictionary<RelationshipTypeId, DegreeHistogram>();
        var globalHist      = new DegreeHistogram();
        var globalOut       = new DegreeHistogram();
        var globalIn        = new DegreeHistogram();
        var degreeBuilder   = new NodeDegreeLookup.Builder(powerNodeThreshold, denseThreshold);
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

            // 両方向のリレーションシップチェーンを走査する。
            // source 側の訪問時はリレーションシップのプロパティも処理することで、
            // プロパティ収集の観点で各リレーションシップを 1 回ずつ訪問する。
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

            degreeBuilder.Record(nodeId, outDegree, inDegree);

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
            NodeDegrees           = degreeBuilder.Build(),
            PropertyKeys          = propertyKeys,
            TotalNodes            = totalNodes,
            TotalRelationships    = totalRels,
            HasFastLabelIndex     = tx.Access.HasFastLabelIndex,
        };
    }
}

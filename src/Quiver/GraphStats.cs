using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// ノードの次数分布を対数的バケット (0, 1, ≤3, ≤7, … ≤255, ∞) で集計するヒストグラム。
/// オプティマイザの fan-out 推定や power node 判定の素材になる。
/// </summary>
public sealed class DegreeHistogram
{
    // Bucket upper-bounds: 0, 1, 3, 7, 15, 31, 63, 127, 255, ∞
    private readonly long[] _counts = new long[10];

    /// <summary>記録されたノード数。</summary>
    public long TotalNodes { get; private set; }
    /// <summary>全ノードの次数の総和。</summary>
    public long TotalDegree { get; private set; }
    /// <summary>観測された最大次数。</summary>
    public long MaxDegree { get; private set; }
    /// <summary>平均次数 (ノード数 0 のときは 0)。</summary>
    public double MeanDegree => TotalNodes == 0 ? 0.0 : (double)TotalDegree / TotalNodes;
    /// <summary>バケットごとのノード件数 (上限境界は 0,1,3,7,15,31,63,127,255,∞)。</summary>
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
/// "power node" (総次数が <see cref="GraphStats.PowerNodeDegreeThreshold"/> を超えるノード) ごとに
/// 記録される次数サマリ。
/// </summary>
/// <param name="NodeId">対象ノード。</param>
/// <param name="OutDegree">出次数。</param>
/// <param name="InDegree">入次数。</param>
public readonly record struct NodeDegreeSummary(
    NodeId NodeId,
    long OutDegree,
    long InDegree)
{
    /// <summary>出次数 + 入次数。</summary>
    public long TotalDegree => OutDegree + InDegree;
}

/// <summary>
/// プロパティキーごとの統計。<see cref="GraphStats.Collect"/> が収集する。
/// </summary>
public sealed class PropertyKeyStats
{
    private readonly HashSet<long> _distinctScalars = [];
    private readonly HashSet<string> _distinctStrings = [];
    private bool _distinctSaturated;

    /// <summary><see cref="DistinctEstimate"/> が概算へ切り替わる前に正確に追跡する distinct 値の上限。</summary>
    public const int DistinctTrackingCap = 4096;

    /// <summary>対象のプロパティキー ID。</summary>
    public PropertyKeyId KeyId { get; init; }

    /// <summary>
    /// このキーに対して観測された全値の <see cref="PropertyTypeFlags"/> ビット和。
    /// 数値述語 / 文字列述語が原理的にマッチし得るかをオプティマイザが判定するのに使う。
    /// </summary>
    public PropertyTypeFlags ObservedTypes { get; private set; }

    /// <summary>このキーで観測されたプロパティ出現の総数 (ノード + リレーションシップ横断)。</summary>
    public long Count { get; private set; }

    /// <summary>走査範囲内でこのキーを設定していなかったエンティティ数。</summary>
    public long NullOrMissingCount { get; private set; }

    /// <summary>
    /// distinct 値数の概算。<see cref="DistinctTrackingCap"/> 以下では正確。
    /// 飽和後は cap にクランプされる (HyperLogLog 化は後続課題)。
    /// </summary>
    public long DistinctEstimate { get; private set; }

    /// <summary>観測された整数値の最小 (未観測時は <see cref="long.MaxValue"/>)。</summary>
    public long MinInt64 { get; private set; } = long.MaxValue;
    /// <summary>観測された整数値の最大 (未観測時は <see cref="long.MinValue"/>)。</summary>
    public long MaxInt64 { get; private set; } = long.MinValue;
    /// <summary>観測された浮動小数点値の最小 (未観測時は +∞)。</summary>
    public double MinDouble { get; private set; } = double.PositiveInfinity;
    /// <summary>観測された浮動小数点値の最大 (未観測時は -∞)。</summary>
    public double MaxDouble { get; private set; } = double.NegativeInfinity;

    /// <summary>整数の範囲統計 (<see cref="MinInt64"/>/<see cref="MaxInt64"/>) が利用可能か。</summary>
    public bool HasNumericRange =>
        (ObservedTypes & PropertyTypeFlags.Numeric) != 0 && MinInt64 != long.MaxValue;

    /// <summary>浮動小数点の範囲統計 (<see cref="MinDouble"/>/<see cref="MaxDouble"/>) が利用可能か。</summary>
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

/// <summary>
/// グラフ全体の統計スナップショット。ラベル基数 / エッジ型頻度 / 次数分布 / プロパティ統計を保持し、
/// クエリオプティマイザのコスト推定に使う。<see cref="GraphDatabase.CollectStats()"/> で収集する。
/// </summary>
public sealed class GraphStats
{
    /// <summary>この値を超える総次数のノードを <see cref="PowerNodes"/> に記録する既定しきい値。</summary>
    public const int PowerNodeDegreeThreshold = 256;

    /// <summary>空の統計 (全カウントが 0)。統計未収集時のフォールバック。</summary>
    public static readonly GraphStats Empty = new();

    /// <summary>ラベルごとのノード件数。</summary>
    public IReadOnlyDictionary<LabelId, long> LabelCardinality { get; private init; }
        = new Dictionary<LabelId, long>();
    /// <summary>リレーションシップ型ごとのエッジ件数。</summary>
    public IReadOnlyDictionary<RelationshipTypeId, long> EdgeTypeFrequency { get; private init; }
        = new Dictionary<RelationshipTypeId, long>();

    /// <summary>全ノードの総次数 (出+入) ヒストグラム。</summary>
    public DegreeHistogram GlobalDegreeHistogram { get; private init; } = new();
    /// <summary>全ノードの出次数ヒストグラム。</summary>
    public DegreeHistogram GlobalOutDegree { get; private init; } = new();
    /// <summary>全ノードの入次数ヒストグラム。</summary>
    public DegreeHistogram GlobalInDegree { get; private init; } = new();

    /// <summary>ラベルごとの総次数ヒストグラム。</summary>
    public IReadOnlyDictionary<LabelId, DegreeHistogram> DegreeByLabel { get; private init; }
        = new Dictionary<LabelId, DegreeHistogram>();
    /// <summary>リレーションシップ型ごとの出次数ヒストグラム。</summary>
    public IReadOnlyDictionary<RelationshipTypeId, DegreeHistogram> OutDegreeByType { get; private init; }
        = new Dictionary<RelationshipTypeId, DegreeHistogram>();
    /// <summary>リレーションシップ型ごとの入次数ヒストグラム。</summary>
    public IReadOnlyDictionary<RelationshipTypeId, DegreeHistogram> InDegreeByType { get; private init; }
        = new Dictionary<RelationshipTypeId, DegreeHistogram>();

    /// <summary>
    /// power node の辞書ビュー (後方互換)。<see cref="NodeDegrees"/> を裏に持ち、
    /// 呼び出されるまで遅延 materialize するため dense モードでは辞書コストを払わない。
    /// </summary>
    public IReadOnlyDictionary<NodeId, NodeDegreeSummary> PowerNodes
        => _powerNodesCache ??= NodeDegrees.SnapshotPowerNodes();
    private IReadOnlyDictionary<NodeId, NodeDegreeSummary>? _powerNodesCache;

    /// <summary>
    /// ノード毎 degree のルックアップ。観測した <c>NodeId</c> 空間が dense なら直接配列、
    /// sparse なら辞書フォールバック。ホットパスでは <see cref="PowerNodes"/> ではなくこちらを使う。
    /// </summary>
    public NodeDegreeLookup NodeDegrees { get; private init; } = NodeDegreeLookup.Empty;

    /// <summary>プロパティキーごとの統計。</summary>
    public IReadOnlyDictionary<PropertyKeyId, PropertyKeyStats> PropertyKeys { get; private init; }
        = new Dictionary<PropertyKeyId, PropertyKeyStats>();

    /// <summary>収集時点の総ノード数。</summary>
    public long TotalNodes { get; private init; }
    /// <summary>収集時点の総リレーションシップ数。</summary>
    public long TotalRelationships { get; private init; }

    /// <summary>
    /// 観測対象 backend が <c>NodeByLabelScan</c> を O(|L|) で提供できるか。
    /// バイナリ backend で <c>LabelNodeIndex</c> sidecar が接続されているとき <c>true</c>。
    /// <c>InlineGraphAccessMethods</c> 経路 / ANN bypass 等 sidecar 無し backend では <c>false</c>。
    /// <see cref="Quiver.Client.Internal.PendingKnnBuilder"/> の push-down 閾値判定で、
    /// dim-aware piecewise table を引くか legacy 30% 単一閾値を引くかを切り替えるのに使う。
    /// テストから明示的に <c>false</c> 経路を再現できるよう <c>init</c> を公開している。
    /// </summary>
    public bool HasFastLabelIndex { get; init; }

    /// <summary>指定ラベルの推定基数 (未観測は 0)。</summary>
    public long EstimateCardinality(LabelId label)
        => LabelCardinality.TryGetValue(label, out var n) ? n : 0;

    /// <summary>指定ラベルの推定選択率 (基数 / 総ノード数)。</summary>
    public double EstimateSelectivity(LabelId label)
        => TotalNodes == 0 ? 0.0 : (double)EstimateCardinality(label) / TotalNodes;

    /// <summary>指定ラベルの推定平均次数 (未観測時はグローバル平均にフォールバック)。</summary>
    public double EstimateMeanDegree(LabelId label)
        => DegreeByLabel.TryGetValue(label, out var h) ? h.MeanDegree : GlobalDegreeHistogram.MeanDegree;

    /// <summary>
    /// 指定フィルタ下での 1 ホップ展開のノード毎 fan-out を推定する。
    /// 最も具体的なシグナル (方向 + 型) からグローバル平均へ段階的にフォールバックする。
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
    /// <see cref="NodeDegrees"/> による O(1) の power node 判定 (dense はビットテスト、sparse は辞書ルックアップ)。
    /// </summary>
    public bool IsLikelyPowerNode(NodeId nodeId) => NodeDegrees.IsLikelyPowerNode(nodeId);

    /// <summary>
    /// O(1) の degree ルックアップ。ノード ID が dense 範囲外 (または sparse モードで未追跡の
    /// power node でない) ときは <c>false</c> を返す — 呼び出し側は <c>false</c> を「次数 0」と
    /// 解釈してはならない。
    /// </summary>
    public bool TryGetDegree(NodeId nodeId, out long outDegree, out long inDegree)
        => NodeDegrees.TryGetDegree(nodeId, out outDegree, out inDegree);

    /// <summary>
    /// 既存 stats から <see cref="HasFastLabelIndex"/> bit のみを差し替えた複製を返す。
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

    // ITransaction は内部型のため Collect(ITransaction ...) は internal。
    // 公開経路は GraphDatabase.CollectStats()。
    internal static GraphStats Collect(ITransaction tx) => Collect(tx, PowerNodeDegreeThreshold);

    internal static GraphStats Collect(ITransaction tx, int powerNodeThreshold)
        => Collect(tx, powerNodeThreshold, NodeDegreeLookup.DefaultDenseThreshold);

    // dense/sparse の切替閾値を呼び出し側で調整できるオーバーロード。既定の
    // NodeDegreeLookup.DefaultDenseThreshold (4.0) は bulk-load 済みグラフ向け。
    // sparse fallback を意図的に試すテストは < 1.0 を渡す。
    internal static GraphStats Collect(ITransaction tx, int powerNodeThreshold, double denseThreshold)
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

                    // ARCH-5c Phase 4: rel の inline + overflow を結合列挙する。
                    var rpe = tx.Relationships.EnumerateProperties(relId, tx.Properties);
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

            // Node properties → PropertyKeyStats (ARCH-5c: inline + overflow)
            var propEnum = tx.Nodes.EnumerateProperties(nodeId, tx.Properties);
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

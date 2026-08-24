using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu;

/// <summary>
/// Vertexの次数分布を対数的バケット (0, 1, ≤3, ≤7, … ≤255, ∞) で集計するヒストグラム。
/// オプティマイザの fan-out 推定や power vertex 判定の素材になる。
/// </summary>
internal sealed class DegreeHistogram
{
    // バケット上限: 0, 1, 3, 7, 15, 31, 63, 127, 255, ∞
    private readonly long[] _counts = new long[10];

    /// <summary>記録されたVertex数。</summary>
    public long TotalVertices { get; private set; }
    /// <summary>全Vertexの次数の総和。</summary>
    public long TotalDegree { get; private set; }
    /// <summary>観測された最大次数。</summary>
    public long MaxDegree { get; private set; }
    /// <summary>平均次数 (Vertex数 0 のときは 0)。</summary>
    public double MeanDegree => TotalVertices == 0 ? 0.0 : (double)TotalDegree / TotalVertices;
    /// <summary>バケットごとのVertex件数 (上限境界は 0,1,3,7,15,31,63,127,255,∞)。</summary>
    public IReadOnlyList<long> BucketCounts => _counts;

    internal void Record(long degree)
    {
        TotalVertices++;
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
/// "power vertex" (総次数が <see cref="GraphStats.PowerVertexDegreeThreshold"/> を超えるVertex) ごとに
/// 記録される次数サマリ。
/// </summary>
/// <param name="VertexId">対象Vertex。</param>
/// <param name="OutDegree">出次数。</param>
/// <param name="InDegree">入次数。</param>
internal readonly record struct VertexDegreeSummary(
    VertexId VertexId,
    long OutDegree,
    long InDegree)
{
    /// <summary>出次数 + 入次数。</summary>
    public long TotalDegree => OutDegree + InDegree;
}

/// <summary>
/// Nexusのメンバー数ごとの件数分布。
/// オプティマイザが型ごとの展開 fan-out を見積もるための厳密な arity を保持する。
/// </summary>
internal sealed class ArityHistogram
{
    private readonly SortedDictionary<int, long> _counts = new();

    /// <summary>記録したNexus件数。</summary>
    public long TotalNexuses { get; private set; }

    /// <summary>全Nexusの incidence 総数。</summary>
    public long TotalIncidences { get; private set; }

    /// <summary>観測した最大 arity。空の場合は 0。</summary>
    public int MaxArity { get; private set; }

    /// <summary>平均 arity。空の場合は 0。</summary>
    public double MeanArity
        => TotalNexuses == 0 ? 0.0 : (double)TotalIncidences / TotalNexuses;

    /// <summary>arity をキー、Nexus件数を値とする分布。</summary>
    public IReadOnlyDictionary<int, long> Counts => _counts;

    internal void Record(int arity)
    {
        TotalNexuses++;
        TotalIncidences += arity;
        if (arity > MaxArity) MaxArity = arity;
        _counts.TryGetValue(arity, out long count);
        _counts[arity] = count + 1;
    }
}

/// <summary>
/// プロパティキーごとの統計。<see cref="GraphStats.Collect"/> が収集する。
/// </summary>
internal sealed class PropertyKeyStats
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

    /// <summary>このキーで観測されたプロパティ出現の総数 (Vertex + Edge横断)。</summary>
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
                // メモリ使用量を抑えるため、Bytes の distinct 追跡は意図的に省略する。
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
/// クエリオプティマイザのコスト推定に使う。<see cref="YatagarasuDatabase.CollectStats()"/> で収集する。
/// </summary>
internal sealed class GraphStats
{
    /// <summary>この値を超える総次数のVertexを <see cref="PowerVertices"/> に記録する既定しきい値。</summary>
    public const int PowerVertexDegreeThreshold = 256;

    /// <summary>空の統計 (全カウントが 0)。統計未収集時のフォールバック。</summary>
    public static readonly GraphStats Empty = new();

    /// <summary>ラベルごとのVertex件数。</summary>
    public IReadOnlyDictionary<LabelId, long> LabelCardinality { get; private init; }
        = new Dictionary<LabelId, long>();
    /// <summary>Edge型ごとのエッジ件数。</summary>
    public IReadOnlyDictionary<EdgeTypeId, long> EdgeTypeFrequency { get; private init; }
        = new Dictionary<EdgeTypeId, long>();
    /// <summary>Nexus型ごとの件数。</summary>
    public IReadOnlyDictionary<NexusTypeId, long> NexusTypeFrequency { get; private init; }
        = new Dictionary<NexusTypeId, long>();
    /// <summary>Nexus型ごとの arity 分布。</summary>
    public IReadOnlyDictionary<NexusTypeId, ArityHistogram> NexusArityByType { get; private init; }
        = new Dictionary<NexusTypeId, ArityHistogram>();

    /// <summary>全Vertexの総次数 (出+入) ヒストグラム。</summary>
    public DegreeHistogram GlobalDegreeHistogram { get; private init; } = new();
    /// <summary>全Vertexの出次数ヒストグラム。</summary>
    public DegreeHistogram GlobalOutDegree { get; private init; } = new();
    /// <summary>全Vertexの入次数ヒストグラム。</summary>
    public DegreeHistogram GlobalInDegree { get; private init; } = new();

    /// <summary>ラベルごとの総次数ヒストグラム。</summary>
    public IReadOnlyDictionary<LabelId, DegreeHistogram> DegreeByLabel { get; private init; }
        = new Dictionary<LabelId, DegreeHistogram>();
    /// <summary>Edge型ごとの出次数ヒストグラム。</summary>
    public IReadOnlyDictionary<EdgeTypeId, DegreeHistogram> OutDegreeByType { get; private init; }
        = new Dictionary<EdgeTypeId, DegreeHistogram>();
    /// <summary>Edge型ごとの入次数ヒストグラム。</summary>
    public IReadOnlyDictionary<EdgeTypeId, DegreeHistogram> InDegreeByType { get; private init; }
        = new Dictionary<EdgeTypeId, DegreeHistogram>();

    /// <summary>
    /// power vertex の辞書ビュー (後方互換)。<see cref="VertexDegrees"/> を裏に持ち、
    /// 呼び出されるまで遅延 materialize するため dense モードでは辞書コストを払わない。
    /// </summary>
    public IReadOnlyDictionary<VertexId, VertexDegreeSummary> PowerVertices
        => _powerVerticesCache ??= VertexDegrees.SnapshotPowerVertices();
    private IReadOnlyDictionary<VertexId, VertexDegreeSummary>? _powerVerticesCache;

    /// <summary>
    /// Vertex毎 degree のルックアップ。観測した <c>VertexId</c> 空間が dense なら直接配列、
    /// sparse なら辞書フォールバック。ホットパスでは <see cref="PowerVertices"/> ではなくこちらを使う。
    /// </summary>
    public VertexDegreeLookup VertexDegrees { get; private init; } = VertexDegreeLookup.Empty;

    /// <summary>プロパティキーごとの統計。</summary>
    public IReadOnlyDictionary<PropertyKeyId, PropertyKeyStats> PropertyKeys { get; private init; }
        = new Dictionary<PropertyKeyId, PropertyKeyStats>();

    /// <summary>収集時点の総Vertex数。</summary>
    public long TotalVertices { get; private init; }
    /// <summary>収集時点の総Edge数。</summary>
    public long TotalEdges { get; private init; }
    /// <summary>収集時点の総Nexus数。</summary>
    public long TotalNexuses { get; private init; }

    /// <summary>
    /// 全文索引ごとの BM25 コーパス統計 (N / avgdl) スナップショット。索引名でキーする。
    /// <c>g.Search</c> / <c>.FilterByText</c> が operator へ N/avgdl を渡し、クエリ毎の O(N) norms
    /// 走査を省く (BM25 は統計鮮度に頑健なので定期収集・近似で足りる)。internal 専用
    /// (<see cref="Bm25CorpusStats"/> が internal、公開サーフェスは増やさない)。
    /// </summary>
    internal IReadOnlyDictionary<string, Bm25CorpusStats> FullTextCorpora { get; private init; }
        = new Dictionary<string, Bm25CorpusStats>(StringComparer.Ordinal);

    /// <summary>指定全文索引のコーパス統計 (未収集は null)。</summary>
    internal Bm25CorpusStats? FullTextCorpus(string indexName)
        => FullTextCorpora.TryGetValue(indexName, out var c) ? c : null;

    /// <summary>
    /// 観測対象 backend が <c>VertexByLabelScan</c> を O(|L|) で提供できるか。
    /// バイナリ backend で <c>LabelVertexIndex</c> sidecar が接続されているとき <c>true</c>。
    /// <c>InlineGraphAccessMethods</c> 経路 / ANN bypass 等 sidecar 無し backend では <c>false</c>。
    /// optimizer の push-down 閾値判定で、dim-aware piecewise table を引くか
    /// sidecar-free の保守的な単一閾値を引くかを切り替えるのに使う。
    /// テストから明示的に <c>false</c> 経路を再現できるよう <c>init</c> を公開している。
    /// </summary>
    public bool HasFastLabelIndex { get; init; }

    /// <summary>指定ラベルの推定基数 (未観測は 0)。</summary>
    public long EstimateCardinality(LabelId label)
        => LabelCardinality.TryGetValue(label, out var n) ? n : 0;

    /// <summary>指定ラベルの推定選択率 (基数 / 総Vertex数)。</summary>
    public double EstimateSelectivity(LabelId label)
        => TotalVertices == 0 ? 0.0 : (double)EstimateCardinality(label) / TotalVertices;

    /// <summary>指定ラベルの推定平均次数 (未観測時はグローバル平均にフォールバック)。</summary>
    public double EstimateMeanDegree(LabelId label)
        => DegreeByLabel.TryGetValue(label, out var h) ? h.MeanDegree : GlobalDegreeHistogram.MeanDegree;

    /// <summary>
    /// 指定フィルタ下での 1 ホップ展開のVertex毎 fan-out を推定する。
    /// 最も具体的なシグナル (方向 + 型) からグローバル平均へ段階的にフォールバックする。
    /// </summary>
    internal double EstimateFanOut(LabelId? sourceLabel, EdgeTypeId? typeFilter, Direction direction)
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

            // ヒストグラムが空のとき (例えば該当型は存在するがサンプル範囲のVertexでは観測されないとき)
            // は、グローバルなエッジ型頻度 / TotalVertices へフォールバックする。
            if (mean == 0.0 && TotalVertices > 0 &&
                EdgeTypeFrequency.TryGetValue(typeFilter.Value, out var edgeCount))
            {
                mean = direction == Direction.Both
                    ? 2.0 * edgeCount / TotalVertices
                    : (double)edgeCount / TotalVertices;
            }
            return mean;
        }

        // 型フィルターがなければ、利用可能な方向別グローバルヒストグラムを使う。
        return direction switch
        {
            Direction.Outgoing => GlobalOutDegree.MeanDegree,
            Direction.Incoming => GlobalInDegree.MeanDegree,
            _ => GlobalDegreeHistogram.MeanDegree,
        };
    }

    /// <summary>
    /// <see cref="VertexDegrees"/> による O(1) の power vertex 判定 (dense はビットテスト、sparse は辞書ルックアップ)。
    /// </summary>
    public bool IsLikelyPowerVertex(VertexId vertexId) => VertexDegrees.IsLikelyPowerVertex(vertexId);

    /// <summary>
    /// O(1) の degree ルックアップ。Vertex ID が dense 範囲外 (または sparse モードで未追跡の
    /// power vertex でない) ときは <c>false</c> を返す — 呼び出し側は <c>false</c> を「次数 0」と
    /// 解釈してはならない。
    /// </summary>
    public bool TryGetDegree(VertexId vertexId, out long outDegree, out long inDegree)
        => VertexDegrees.TryGetDegree(vertexId, out outDegree, out inDegree);

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
            NexusTypeFrequency = NexusTypeFrequency,
            NexusArityByType  = NexusArityByType,
            GlobalDegreeHistogram = GlobalDegreeHistogram,
            GlobalOutDegree       = GlobalOutDegree,
            GlobalInDegree        = GlobalInDegree,
            DegreeByLabel         = DegreeByLabel,
            OutDegreeByType       = OutDegreeByType,
            InDegreeByType        = InDegreeByType,
            VertexDegrees           = VertexDegrees,
            PropertyKeys          = PropertyKeys,
            TotalVertices            = TotalVertices,
            TotalEdges    = TotalEdges,
            TotalNexuses       = TotalNexuses,
            FullTextCorpora       = FullTextCorpora,
            HasFastLabelIndex     = hasFastLabelIndex,
        };
    }

    /// <summary>
    /// テスト用に最小限の統計スナップショットを構築する。
    /// </summary>
    internal static GraphStats ForTest(
        long totalVertices,
        IReadOnlyDictionary<LabelId, long> labelCardinality,
        bool hasFastLabelIndex = false)
        => new()
        {
            TotalVertices = totalVertices,
            LabelCardinality = labelCardinality,
            HasFastLabelIndex = hasFastLabelIndex,
        };

    // ITransaction は内部型のため Collect(ITransaction ...) は internal。
    // 公開経路は YatagarasuDatabase.CollectStats()。
    internal static GraphStats Collect(ITransaction tx) => Collect(tx, PowerVertexDegreeThreshold);

    internal static GraphStats Collect(ITransaction tx, int powerVertexThreshold)
        => Collect(tx, powerVertexThreshold, VertexDegreeLookup.DefaultDenseThreshold);

    // dense/sparse の切替閾値を呼び出し側で調整できるオーバーロード。既定の
    // VertexDegreeLookup.DefaultDenseThreshold (4.0) は bulk-load 済みグラフ向け。
    // sparse fallback を意図的に試すテストは < 1.0 を渡す。
    internal static GraphStats Collect(ITransaction tx, int powerVertexThreshold, double denseThreshold)
    {
        var labelCard       = new Dictionary<LabelId, long>();
        var edgeFreq        = new Dictionary<EdgeTypeId, long>();
        var nexusFreq   = new Dictionary<NexusTypeId, long>();
        var arityByType     = new Dictionary<NexusTypeId, ArityHistogram>();
        var degreeByLabel   = new Dictionary<LabelId, DegreeHistogram>();
        var outDegreeByType = new Dictionary<EdgeTypeId, DegreeHistogram>();
        var inDegreeByType  = new Dictionary<EdgeTypeId, DegreeHistogram>();
        var globalHist      = new DegreeHistogram();
        var globalOut       = new DegreeHistogram();
        var globalIn        = new DegreeHistogram();
        var degreeBuilder   = new VertexDegreeLookup.Builder(powerVertexThreshold, denseThreshold);
        var propertyKeys    = new Dictionary<PropertyKeyId, PropertyKeyStats>();

        long totalVertices = 0;
        long totalEdges  = 0;
        long totalNexuses = 0;

        // Vertexごとの Dictionary 割り当てを避けるため、型別カウンターを再利用する。
        var perTypeOut = new Dictionary<EdgeTypeId, long>();
        var perTypeIn  = new Dictionary<EdgeTypeId, long>();

        foreach (var vertexId in tx.Vertices.Scan())
        {
            var vertex = tx.Vertices.Read(vertexId);
            if (!vertex.InUse) continue;
            var materializer = new EntityIdentityMaterializer(tx.Vertices);
            if (!materializer.TryVertex(vertexId, out var logicalVertexId))
                continue;

            totalVertices++;
            var label = vertex.Label;

            labelCard.TryGetValue(label, out var lc);
            labelCard[label] = lc + 1;

            if (!degreeByLabel.ContainsKey(label))
                degreeByLabel[label] = new DegreeHistogram();

            perTypeOut.Clear();
            perTypeIn.Clear();
            long outDegree = 0;
            long inDegree  = 0;

            // 両方向のEdgeチェーンを走査する。
            // source 側の訪問時はEdgeのプロパティも処理することで、
            // プロパティ収集の観点で各Edgeを 1 回ずつ訪問する。
            var edgeId = vertex.FirstEdgeId;
            while (edgeId.IsValid)
            {
                var edge = tx.Edges.Read(edgeId);
                bool isSource = edge.Source.Sequence == vertexId.Sequence;
                var nextId = isSource ? edge.SourceNext : edge.TargetNext;

                if (isSource)
                {
                    totalEdges++;
                    edgeFreq.TryGetValue(edge.Type, out var tc);
                    edgeFreq[edge.Type] = tc + 1;

                    outDegree++;
                    perTypeOut.TryGetValue(edge.Type, out var po);
                    perTypeOut[edge.Type] = po + 1;

            // edge の inline + overflow を結合列挙する。
                    var rpe = tx.Edges.EnumerateProperties(edgeId, tx.Properties);
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
                    perTypeIn.TryGetValue(edge.Type, out var pi);
                    perTypeIn[edge.Type] = pi + 1;
                }

                edgeId = nextId;
            }

            long vertexDegree = outDegree + inDegree;
            globalHist.Record(vertexDegree);
            globalOut.Record(outDegree);
            globalIn.Record(inDegree);
            degreeByLabel[label].Record(vertexDegree);

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

            degreeBuilder.Record(logicalVertexId, outDegree, inDegree);

            // Vertexプロパティ → PropertyKeyStats (inline + overflow)
            var propEnum = tx.Vertices.EnumerateProperties(vertexId, tx.Properties);
            while (propEnum.MoveNext())
            {
                var ph = propEnum.Current;
                if (!propertyKeys.TryGetValue(ph.KeyId, out var pks))
                    propertyKeys[ph.KeyId] = pks = new PropertyKeyStats { KeyId = ph.KeyId };
                pks.Observe(ph.Value);
            }
        }

        // メンバー集合は作成後不変なので header ごとに chain を 1 回だけ走査すれば
        // 型別件数と arity を同時に収集できる。プロパティも同じ走査で統計へ合流させる。
        foreach (NexusId nexusId in tx.Nexuses.Scan())
        {
            using var header = tx.Nexuses.Read(nexusId);
            if (!header.InUse) continue;

            totalNexuses++;
            nexusFreq.TryGetValue(header.Type, out long typeCount);
            nexusFreq[header.Type] = typeCount + 1;

            int arity = 0;
            var incidence = tx.Incidences.EnumerateByNexus(nexusId, tx.Nexuses);
            while (incidence.MoveNext()) arity++;

            if (!arityByType.TryGetValue(header.Type, out var histogram))
                arityByType[header.Type] = histogram = new ArityHistogram();
            histogram.Record(arity);

            var properties = tx.Nexuses.EnumerateProperties(nexusId, tx.Properties);
            while (properties.MoveNext())
            {
                var property = properties.Current;
                if (!propertyKeys.TryGetValue(property.KeyId, out var keyStats))
                    propertyKeys[property.KeyId] = keyStats = new PropertyKeyStats { KeyId = property.KeyId };
                keyStats.Observe(property.Value);
            }
        }

        // 各エンティティはキーごとに最大 1 値だけを持つため、
        // missing = totalEntities - observedCount となる。
        long totalEntities = totalVertices + totalEdges + totalNexuses;
        foreach (var pks in propertyKeys.Values)
        {
            long missing = totalEntities - pks.Count;
            pks.SetNullOrMissingCount(missing < 0 ? 0 : missing);
        }

        // 全文索引ごとの BM25 コーパス統計をスナップショットする。
        // 索引ごとに postings と norms を 1 回ずつ走査し、演算子がクエリごとに行う O(N) 走査を避ける。
        // 同時に WAND が必要とする語ごとの (df, maxTf) と minDocLen を供給する。
        var ftCorpora = new Dictionary<string, Bm25CorpusStats>(StringComparer.Ordinal);
        foreach (var (name, _, _, _) in tx.Indexes.ListFullTextDefinitions())
        {
            if (tx.FullTextSegments is null
                || !tx.FullTextSegments.TryOpen(tx, name, out var ft))
                continue;
            var (terms, minDocLen, docCount, totalTokens) = ft.CollectTermStats();
            double avgdl = docCount > 0 ? (double)totalTokens / docCount : 0.0;
            ftCorpora[name] = new Bm25CorpusStats(docCount, avgdl, new Bm25TermStats(terms, minDocLen));
        }

        return new GraphStats
        {
            LabelCardinality      = labelCard,
            EdgeTypeFrequency     = edgeFreq,
            NexusTypeFrequency = nexusFreq,
            NexusArityByType  = arityByType,
            GlobalDegreeHistogram = globalHist,
            GlobalOutDegree       = globalOut,
            GlobalInDegree        = globalIn,
            DegreeByLabel         = degreeByLabel,
            OutDegreeByType       = outDegreeByType,
            InDegreeByType        = inDegreeByType,
            VertexDegrees           = degreeBuilder.Build(),
            PropertyKeys          = propertyKeys,
            TotalVertices            = totalVertices,
            TotalEdges    = totalEdges,
            TotalNexuses       = totalNexuses,
            FullTextCorpora       = ftCorpora,
            HasFastLabelIndex     = tx.Access.HasFastLabelIndex,
        };
    }
}

using Quiver.Core;
using Quiver.Query.Physical;

namespace Quiver.Api.Internal;

/// <summary>
/// VEC-9: <c>g.Knn(...)</c> 直後の遅延 builder。後続が pure-filter (HasLabel/Has/Where/...) や
/// <c>Limit</c> なら candidate-side / k を更新した新インスタンスに置き換え、non-pure step や
/// terminal で <see cref="Materialize"/> によって <see cref="KnnNodeSourceBuilder"/> (vector-first)
/// または <see cref="FilteredKnnNodeSourceBuilder"/> (graph-first) に確定する。
/// <para>
/// VEC-10: <see cref="Materialize(GraphStats?, ISchemaApi?)"/> に <see cref="GraphStats"/> を渡せば、
/// 構造ヒントが graph-first を示唆しても label cardinality が高い (既定 30% 超) ときは
/// vector-first にフォールバックして wall-clock 劣化を回避する。フォールバック時は
/// candidate-side に積まれていた filter chain (label predicate を含む) を
/// <see cref="KnnNodeSourceBuilder"/> の後段に再配置するため意味論は維持される。
/// </para>
/// </summary>
internal sealed class PendingKnnBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _candidate;
    private readonly string _indexName;
    private readonly float[] _query;
    private readonly int _k;
    // VEC-12: 0 = unknown (= legacy 30% 一本に倒れる)。> 0 のとき dim-aware piecewise table を引く。
    private readonly int _dim;

    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal IOperatorBuilder Candidate => _candidate;
    internal string IndexName => _indexName;
    internal ReadOnlySpan<float> Query => _query;
    internal int K => _k;
    internal int Dim => _dim;

    /// <summary>
    /// VEC-10: 構造ヒントが graph-first を示唆していても、label cardinality / TotalNodes が
    /// この値以上なら vector-first にフォールバックする (legacy 経路)。VEC-11 で
    /// <see cref="GraphStats.HasFastLabelIndex"/> = <c>true</c> の backend では
    /// <see cref="FastLabelIndexThresholds"/> から dim-aware 値を引くため、この定数は
    /// sidecar 不在 backend (<c>InlineGraphAccessMethods</c> / 単体テスト経路) でのみ使われる。
    /// </summary>
    internal const double VectorFirstLabelFraction = 0.30;

    /// <summary>
    /// VEC-12: <see cref="GraphStats.HasFastLabelIndex"/> = <c>true</c> 経路で参照する
    /// dim → fraction 上限の昇順 piecewise table。各 dim 上限 (含む) のバケットごとに、
    /// label cardinality / TotalNodes がこの値以上なら vector-first にフォールバックする。
    /// <para>
    /// 出典: <c>KnnPushdownThresholdSweepBenchmarks</c> の 5 dim × 8 sel 実測
    /// ([docs/benchmarks/2026-05-20_VEC-12_after.md](docs/benchmarks/2026-05-20_VEC-12_after.md))。
    /// 各 dim で <c>GraphFirstForced.Mean == VectorFirstForced.Mean</c> となる sel を線形補間で求め、
    /// 安全マージン 0.05 を引いた値を採用した:
    /// </para>
    /// <list type="bullet">
    ///   <item>dim ≤ 512 (MiniLM-384 等): graph-first が全 sel で vector-first に劣後 (SIMD スコアが安価で
    ///   O(N) 走査が軽い) → 観測 crossover &lt; 0.30。閾値は VEC-10 と同じ k-starvation correctness floor 0.30。</item>
    ///   <item>dim ≤ 1024 (ada-002-768 / Cohere-1024): dim=768 で crossover ρ≈0.52 → 0.47。</item>
    ///   <item>dim ≤ 2048 (text-embedding-3-small-1536): dim=1536 で crossover ρ≈0.75 → 0.70。</item>
    ///   <item>dim &gt; 2048 (text-embedding-3-large-3072): dim=3072 は測定全域 (ρ≤0.80) で graph-first 優位 → 0.80。</item>
    /// </list>
    /// <para>
    /// クロスオーバーは dim に対して単調増加 (本ファイル先頭の cost 比導出どおり)。バケット境界は
    /// 実測 dim を代表点に取り、512 / 1024 / 2048 の丸い値に揃えた。
    /// </para>
    /// </summary>
    private static readonly (int MaxDim, double Threshold)[] FastLabelIndexThresholds =
    {
        (512,           0.30),
        (1024,          0.47),
        (2048,          0.70),
        (int.MaxValue,  0.80),
    };

    internal PendingKnnBuilder(IOperatorBuilder candidate, string indexName, ReadOnlySpan<float> query, int k)
        : this(candidate, indexName, query, k, dim: 0)
    {
    }

    internal PendingKnnBuilder(IOperatorBuilder candidate, string indexName, ReadOnlySpan<float> query, int k, int dim)
    {
        _candidate = candidate;
        _indexName = indexName;
        _query = query.ToArray();
        _k = k;
        _dim = dim;
    }

    /// <summary>同じ <see cref="_query"/> 配列を共有しつつ candidate を差し替えた新 PendingKnn。</summary>
    internal PendingKnnBuilder WithCandidate(IOperatorBuilder candidate)
        => new(candidate, _indexName, _query, _k, _dim, sharedQuery: true);

    /// <summary>K を差し替えた新 PendingKnn (Limit shrink 用)。</summary>
    internal PendingKnnBuilder WithK(int newK)
        => new(_candidate, _indexName, _query, newK, _dim, sharedQuery: true);

    // 配列を再コピーしない private ctor。WithCandidate / WithK 経由でのみ呼ばれる。
    private PendingKnnBuilder(IOperatorBuilder candidate, string indexName, float[] query, int k, int dim, bool sharedQuery)
    {
        _candidate = candidate;
        _indexName = indexName;
        _query = query;
        _k = k;
        _dim = dim;
    }

    /// <summary>
    /// VEC-12: <see cref="GraphStats.HasFastLabelIndex"/> = <c>true</c> 経路で
    /// <paramref name="dim"/> に対応する閾値を <see cref="FastLabelIndexThresholds"/> から線形検索で引く。
    /// <paramref name="dim"/> &lt;= 0 のとき (spec 未解決) は最終バケット (= 最も寛容な閾値) を返し、
    /// 「不明なら vector-first フォールバックを起きにくくする」保守側に倒す。
    /// </summary>
    internal static double FastIndexThresholdForDim(int dim)
    {
        if (dim <= 0)
            return FastLabelIndexThresholds[^1].Threshold;
        foreach (var (maxDim, threshold) in FastLabelIndexThresholds)
        {
            if (dim <= maxDim) return threshold;
        }
        return FastLabelIndexThresholds[^1].Threshold;
    }

    /// <summary>
    /// VEC-9: candidate の構造ヒントで vector-first / graph-first を選択する。
    /// FilterBuilder で囲われている、または ScanBuilder が label 指定済みなら絞り込み済とみなし graph-first。
    /// 素の ScanBuilder() (AllNodes) のままなら現状動作 = vector-first。
    /// </summary>
    internal IOperatorBuilder Materialize() => Materialize(stats: null, schema: null);

    /// <summary>
    /// VEC-10: stats を渡せば label cardinality / TotalNodes が <see cref="VectorFirstLabelFraction"/>
    /// 以上のときに vector-first にフォールバックする。stats が <c>null</c> または label を抽出できない
    /// ケースでは VEC-9 と同じ構造ヒント判定にフォールバックする。
    /// </summary>
    internal IOperatorBuilder Materialize(GraphStats? stats, ISchemaApi? schema)
    {
        if (!HasStructuralSelectivityHint(_candidate))
            return new KnnNodeSourceBuilder(_indexName, _query, _k);

        if (stats is not null && schema is not null && ShouldFallBackToVectorFirst(_candidate, stats, schema))
            return BuildVectorFirstWithReplayedFilters(_candidate, _indexName, _query, _k, schema);

        return new FilteredKnnNodeSourceBuilder(_candidate, _indexName, _query, _k);
    }

    public IPhysicalOperator Build(ISchemaApi schema) => Materialize().Build(schema);

    private static bool HasStructuralSelectivityHint(IOperatorBuilder b)
        => b switch
        {
            FilterBuilder => true,
            ScanBuilder s when s.Label is not null => true,
            _ => false,
        };

    /// <summary>
    /// VEC-10 / VEC-12: candidate チェーンから「最内 <see cref="ScanBuilder"/> の Label」を取り出し、
    /// label cardinality / TotalNodes が閾値以上なら vector-first にフォールバックする。
    /// <para>
    /// 閾値の選択ルール:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="GraphStats.HasFastLabelIndex"/> = <c>true</c> (backend が
    ///     <c>NodeByLabelScan</c> を O(|L|) で提供): VEC-12 の dim-aware piecewise table
    ///     (<see cref="FastIndexThresholdForDim"/>) を引く。dim が大きいほど vector-first
    ///     にフォールバックする閾値が高くなる (= graph-first を選ぶレンジが広くなる)。
    ///   </item>
    ///   <item>
    ///     <see cref="GraphStats.HasFastLabelIndex"/> = <c>false</c>: 旧 VEC-10 動作と完全に
    ///     同じ 30% 単一閾値 (<see cref="VectorFirstLabelFraction"/>) を引く。
    ///   </item>
    /// </list>
    /// </para>
    /// label が抽出できない (Has のみで構成された FilterBuilder チェーン等) ときは
    /// 判断材料が不足するため graph-first を維持する (<c>false</c> を返す)。
    /// </summary>
    private bool ShouldFallBackToVectorFirst(IOperatorBuilder candidate, GraphStats stats, ISchemaApi schema)
    {
        string? label = FindInnermostScanLabel(candidate);
        if (label is null) return false;
        if (stats.TotalNodes <= 0) return false;

        var lid = schema.GetOrCreateLabel(label);
        long card = stats.EstimateCardinality(lid);
        if (card <= 0) return false;

        double fraction = (double)card / stats.TotalNodes;
        double threshold = stats.HasFastLabelIndex
            ? FastIndexThresholdForDim(_dim)
            : VectorFirstLabelFraction;
        return fraction >= threshold;
    }

    private static string? FindInnermostScanLabel(IOperatorBuilder b)
    {
        while (true)
        {
            switch (b)
            {
                case ScanBuilder s:
                    return s.Label;
                case FilterBuilder f:
                    b = f.Source;
                    continue;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// VEC-10 vector-first フォールバック: <see cref="KnnNodeSourceBuilder"/> を起点に、
    /// candidate 側に積まれていた label predicate / pure-filter predicate を post-filter として再配置する。
    /// </summary>
    private static IOperatorBuilder BuildVectorFirstWithReplayedFilters(
        IOperatorBuilder candidate,
        string indexName,
        float[] query,
        int k,
        ISchemaApi schema)
    {
        // candidate を外側 → 内側で walk しつつ、predicate factory を「内側を先に適用する」順に並べる。
        // ScanBuilder("Doc") のような label つきスキャンは、ScanBuilder のラベルを LabelPredicate に
        // 置換して post-filter として復元する (HasLabel が AllNodesScan → NodeByLabelScan 最適化で
        // FilterBuilder を作らずに済んでいたケース対応)。
        var filtersInnerToOuter = new List<Func<ISchemaApi, IPredicate>>();
        IOperatorBuilder b = candidate;
        while (true)
        {
            if (b is FilterBuilder f)
            {
                // 外側の FilterBuilder ほど後で適用する。リストの末尾に追加して、後段で順に積む。
                filtersInnerToOuter.Insert(0, f.PredicateFactory);
                b = f.Source;
                continue;
            }
            if (b is ScanBuilder s)
            {
                if (s.Label is { } labelName)
                {
                    var lid = schema.GetOrCreateLabel(labelName);
                    filtersInnerToOuter.Insert(0, _ => new LabelPredicate(lid, 0));
                }
                break;
            }
            break; // 想定外の builder。フィルタ追加なしで vector-first を返す。
        }

        IOperatorBuilder result = new KnnNodeSourceBuilder(indexName, query, k);
        // KnnNodeSourceBuilder.CurrentEntityColumn == 0 で、candidate も列 0 を出していたため、
        // predicate の column 参照 (col=0) はそのまま再利用できる。
        foreach (var factory in filtersInnerToOuter)
            result = new FilterBuilder(result, factory);
        return result;
    }
}

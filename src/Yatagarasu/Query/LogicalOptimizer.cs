using System.Collections.Immutable;
using Yatagarasu;
using Yatagarasu.Api.Internal;
using Yatagarasu.Core;
using Yatagarasu.Query.Logical;
using Yatagarasu.Query.Physical;

namespace Yatagarasu.Query.Optimizer;

/// <summary>
/// 論理プランに rule + cost ベースの書き換えを施す optimizer。terminal で lower 直後に
/// 1 度実行する。旧 <c>PendingKnnBuilder</c> に埋まっていた KNN 押し下げ判断を
/// ここへ集約した (表層 DSL からは KNN 特別扱いが消える)。
/// </summary>
/// <remarks>
/// rule:
/// <list type="number">
///   <item><b>KnnLimitPushdown</b>: <c>Limit(n, &lt;filters&gt;(Knn(null,K)))</c> → K を <c>min(K,n)</c> に縮め Limit を除去。</item>
///   <item><b>KnnPushdown</b>: <c>&lt;filters&gt;(Knn(null))</c> (filter 1 つ以上) を、構造ヒント + GraphStats から
///   graph-first (<c>Knn(Candidate=&lt;filters&gt;(Scan))</c>) か vector-first (filter を post-filter に据置) に確定。</item>
/// <item><b>FullTextPushdown</b>: <c>&lt;filters&gt;(FullTextScan(null))</c> を KnnPushdown と同型で
///   graph-first (<c>FullTextScan(Candidate=&lt;filters&gt;(Scan))</c>) か text-first に確定。閾値は単一定数
///   <see cref="TextFirstLabelFraction"/> (dim 概念が無いため)。<c>Limit</c> による K 縮小も対称に行う。</item>
///   <item><b>LabelScanRewrite</b>: <c>Filter(LabelPredicate@col0, Scan(Vertex,null))</c> → <c>Scan(Vertex,label)</c>
///   (AllVerticesScan→VertexByLabelScan)。graph-first 候補 + Match 由来プランの両方に効く idempotent rule。</item>
/// </list>
/// </remarks>
internal static class LogicalOptimizer
{
    /// <summary>
    /// 構造ヒントが graph-first を示唆していても、label cardinality / TotalVertices が
    /// この値以上なら vector-first にフォールバックする (sidecar 不在 backend の保守的な単一閾値)。
    /// </summary>
    internal const double VectorFirstLabelFraction = 0.30;

    /// <summary>
    /// <see cref="GraphStats.HasFastLabelIndex"/> = true 経路で参照する dim → fraction 上限の
    /// 昇順 piecewise table。各 dim の crossover に安全マージン 0.05 を引いた値。
    /// </summary>
    private static readonly (int MaxDim, double Threshold)[] FastLabelIndexThresholds =
    {
        (512,          0.30),
        (1024,         0.47),
        (2048,         0.70),
        (int.MaxValue, 0.80),
    };

    /// <summary>
    /// <paramref name="dim"/> に対応する閾値を線形検索で引く。<paramref name="dim"/> &lt;= 0
    /// (spec 未解決) のときは最終バケット (最も寛容) を返し「不明なら vector-first フォールバックを
    /// 起きにくくする」保守側に倒す。
    /// </summary>
    internal static double FastIndexThresholdForDim(int dim)
    {
        if (dim <= 0) return FastLabelIndexThresholds[^1].Threshold;
        foreach (var (maxDim, threshold) in FastLabelIndexThresholds)
            if (dim <= maxDim) return threshold;
        return FastLabelIndexThresholds[^1].Threshold;
    }

    /// <summary>
    /// 構造ヒントが graph-first を示唆していても、label cardinality / TotalVertices が
    /// この値以上なら全文検索を text-first に据え置く保守閾値。BM25 graph-first は df のため
    /// postings を全走査するので、候補集合が十分小さい (低選択率ラベル) ときだけ得をする。KNN と違い
    /// dim 概念が無いため、sidecar 不在時と同じ単一定数を使う。ベンチ実測で調整する余地がある。
    /// </summary>
    internal const double TextFirstLabelFraction = 0.30;

    /// <summary>論理プランを最適化する。<paramref name="stats"/> が null なら KNN / 全文は構造ヒントのみで判定。</summary>
    public static LogicalOp Optimize(LogicalOp plan, GraphStats? stats, ISchemaCatalog schema)
    {
        var p = RewriteKnn(plan, stats, schema);
        p = RewriteFullText(p, stats, schema);
        p = LabelScanRewrite(p, schema);
        return p;
    }

    // ── KnnLimitPushdown + KnnPushdown (再帰的書き換え) ──────────────────────────

    private static LogicalOp RewriteKnn(LogicalOp n, GraphStats? stats, ISchemaCatalog schema)
    {
        // KnnLimitPushdown: Limit(skip=0) が <filters>(Knn(null,K)) の直上にあるなら K を縮め Limit を除去。
        if (n is LimitOp { Skip: 0 } lim
            && TryCollectKnnStack(lim.Source, out var lf, out var lknn) && lknn!.Candidate is null)
        {
            int newK = lim.Limit >= lknn.K ? lknn.K : (int)lim.Limit;
            var shrunk = newK == lknn.K ? lknn : lknn with { K = newK };
            return RewriteKnn(RebuildStack(lf, shrunk), stats, schema);
        }

        // KnnPushdown: <filters>(Knn(null)) で filter が 1 つ以上あるなら graph-first / vector-first を確定。
        if (TryCollectKnnStack(n, out var filters, out var knn) && filters.Count > 0 && knn!.Candidate is null)
            return PushdownKnn(filters, knn, stats, schema);

        return RewriteChildren(n, c => RewriteKnn(c, stats, schema));
    }

    private static LogicalOp PushdownKnn(
        List<Func<ISchemaCatalog, IPredicate>> filters, KnnOp knn, GraphStats? stats, ISchemaCatalog schema)
    {
        // 構造ヒントあり (filters 非空)。stats があり label cardinality が閾値以上なら vector-first へ。
        if (stats is not null && ShouldFallBackToVectorFirst(filters, knn.Dim, stats, schema))
            return RebuildStack(filters, knn); // vector-first: filter は Knn(null) の post-filter のまま

        // graph-first: filter を vertex scan の上に積み直し、label filter を label scan へ畳む。
        LogicalOp candidate = LabelScanRewrite(RebuildStack(filters, new ScanOp(EntityKind.Vertex, null)), schema);
        return knn with { Candidate = candidate };
    }

    private static bool ShouldFallBackToVectorFirst(
        List<Func<ISchemaCatalog, IPredicate>> filters, int dim, GraphStats stats, ISchemaCatalog schema)
    {
        if (stats.TotalVertices <= 0) return false;
        if (FindLabel(filters, schema) is not LabelId lid) return false;
        long card = stats.EstimateCardinality(lid);
        if (card <= 0) return false;

        double fraction = (double)card / stats.TotalVertices;
        double threshold = stats.HasFastLabelIndex ? FastIndexThresholdForDim(dim) : VectorFirstLabelFraction;
        return fraction >= threshold;
    }

    /// <summary>filter 群を materialize し、最初に見つかった col0 の <see cref="LabelPredicate"/> のラベルを返す。</summary>
    private static LabelId? FindLabel(List<Func<ISchemaCatalog, IPredicate>> filters, ISchemaCatalog schema)
    {
        foreach (var f in filters)
            if (f(schema) is LabelPredicate { Column: 0 } lp) return lp.Label;
        return null;
    }

    /// <summary>FilterOp を剥がしながら底の <see cref="KnnOp"/> まで辿る。filter は outer→inner 順で返す。</summary>
    private static bool TryCollectKnnStack(LogicalOp n, out List<Func<ISchemaCatalog, IPredicate>> filters, out KnnOp? knn)
    {
        filters = new List<Func<ISchemaCatalog, IPredicate>>();
        var cur = n;
        while (cur is FilterOp f) { filters.Add(f.PredicateFactory); cur = f.Source; }
        if (cur is KnnOp k) { knn = k; return true; }
        knn = null;
        return false;
    }

    /// <summary>outer→inner 順の filter 群を <paramref name="baseOp"/> の上に元の入れ子で積み直す。</summary>
    private static LogicalOp RebuildStack(List<Func<ISchemaCatalog, IPredicate>> filters, LogicalOp baseOp)
    {
        var result = baseOp;
        for (int i = filters.Count - 1; i >= 0; i--)
            result = new FilterOp(result, filters[i]);
        return result;
    }

    // ── FullTextLimitPushdown + FullTextPushdown (KnnPushdown と同型) ──────

    private static LogicalOp RewriteFullText(LogicalOp n, GraphStats? stats, ISchemaCatalog schema)
    {
        // FullTextLimitPushdown: Limit(skip=0) が <filters>(FullTextScan(null,K)) の直上なら K を縮め Limit を除去。
        if (n is LimitOp { Skip: 0 } lim
            && TryCollectFullTextStack(lim.Source, out var lf, out var lft) && lft!.Candidate is null)
        {
            int newK = lim.Limit >= lft.K ? lft.K : (int)lim.Limit;
            var shrunk = newK == lft.K ? lft : lft with { K = newK };
            return RewriteFullText(RebuildStack(lf, shrunk), stats, schema);
        }

        // FullTextPushdown: <filters>(FullTextScan(null)) で filter が 1 つ以上あるなら graph-first / text-first を確定。
        if (TryCollectFullTextStack(n, out var filters, out var ft) && filters.Count > 0 && ft!.Candidate is null)
            return PushdownFullText(filters, ft, stats, schema);

        return RewriteChildren(n, c => RewriteFullText(c, stats, schema));
    }

    private static LogicalOp PushdownFullText(
        List<Func<ISchemaCatalog, IPredicate>> filters, FullTextScanOp ft, GraphStats? stats, ISchemaCatalog schema)
    {
        // stats があり label cardinality が閾値以上なら text-first に据え置く (filter は post-filter のまま)。
        if (stats is not null && ShouldStayTextFirst(filters, stats, schema))
            return RebuildStack(filters, ft);

        // graph-first: filter を vertex scan の上に積み直し、label filter を label scan へ畳む。
        LogicalOp candidate = LabelScanRewrite(RebuildStack(filters, new ScanOp(EntityKind.Vertex, null)), schema);
        return ft with { Candidate = candidate };
    }

    private static bool ShouldStayTextFirst(
        List<Func<ISchemaCatalog, IPredicate>> filters, GraphStats stats, ISchemaCatalog schema)
    {
        if (stats.TotalVertices <= 0) return false;
        if (FindLabel(filters, schema) is not LabelId lid) return false;
        long card = stats.EstimateCardinality(lid);
        if (card <= 0) return false;
        return (double)card / stats.TotalVertices >= TextFirstLabelFraction;
    }

    /// <summary>FilterOp を剥がしながら底の <see cref="FullTextScanOp"/> まで辿る。filter は outer→inner 順で返す。</summary>
    private static bool TryCollectFullTextStack(
        LogicalOp n, out List<Func<ISchemaCatalog, IPredicate>> filters, out FullTextScanOp? ft)
    {
        filters = new List<Func<ISchemaCatalog, IPredicate>>();
        var cur = n;
        while (cur is FilterOp f) { filters.Add(f.PredicateFactory); cur = f.Source; }
        if (cur is FullTextScanOp s) { ft = s; return true; }
        ft = null;
        return false;
    }

    // ── LabelScanRewrite ────────────────────────────────────────────────────────

    private static LogicalOp LabelScanRewrite(LogicalOp n, ISchemaCatalog schema)
    {
        n = RewriteChildren(n, c => LabelScanRewrite(c, schema));
        if (n is FilterOp { Source: ScanOp { Kind: EntityKind.Vertex, Label: null } } f
            && f.PredicateFactory(schema) is LabelPredicate { Column: 0 } lp)
            return new ScanOp(EntityKind.Vertex, lp.Label);
        return n;
    }

    // ── 子Vertexの汎用書き換え ──────────────────────────────────────────────────

    private static LogicalOp RewriteChildren(LogicalOp n, Func<LogicalOp, LogicalOp> f) => n switch
    {
        FilterOp x               => x with { Source = f(x.Source) },
        ExpandOp x               => x with { Source = f(x.Source) },
        ExpandToNexusOp x    => x with { Source = f(x.Source) },
        ExpandMembersOp x        => x with { Source = f(x.Source) },
        VarLenExpandOp x         => x with { Source = f(x.Source) },
        PathOp x                 => x with { Source = f(x.Source) },
        PropertyLookupOp x       => x with { Source = f(x.Source) },
        LabelNameLookupOp x      => x with { Source = f(x.Source) },
        EdgeEndpointOp x => x with { Source = f(x.Source) },
        LimitOp x                => x with { Source = f(x.Source) },
        SortOp x                 => x with { Source = f(x.Source) },
        DedupOp x                => x with { Source = f(x.Source) },
        BranchOp x               => x with { Source = f(x.Source) },
        ApplyDyadicOp x          => x with { Source = f(x.Source), BPlan = x.BPlan is not null ? f(x.BPlan) : null },
        KnnOp x                  => x.Candidate is null ? x : x with { Candidate = f(x.Candidate) },
        FullTextScanOp x         => x.Candidate is null ? x : x with { Candidate = f(x.Candidate) },
        FusionOp x               => x with { Children = ImmutableArray.CreateRange(x.Children, f) },
        _                        => n, // 葉: ScanOp / VertexSeedOp / CorrelatedInputOp
    };
}

using System.Collections.Immutable;
using Quiver;
using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Physical;

namespace Quiver.Query.Optimizer;

/// <summary>
/// ARCH-7: 論理プランに rule + cost ベースの書き換えを施す optimizer。terminal で lower 直後に
/// 1 度実行する。旧 <c>PendingKnnBuilder</c> に埋まっていた KNN 押し下げ判断 (VEC-9/10/12) を
/// ここへ集約した (表層 DSL からは KNN 特別扱いが消える)。
/// </summary>
/// <remarks>
/// rule:
/// <list type="number">
///   <item><b>KnnLimitPushdown</b>: <c>Limit(n, &lt;filters&gt;(Knn(null,K)))</c> → K を <c>min(K,n)</c> に縮め Limit を除去。</item>
///   <item><b>KnnPushdown</b>: <c>&lt;filters&gt;(Knn(null))</c> (filter 1 つ以上) を、構造ヒント + GraphStats から
///   graph-first (<c>Knn(Candidate=&lt;filters&gt;(Scan))</c>) か vector-first (filter を post-filter に据置) に確定。</item>
///   <item><b>LabelScanRewrite</b>: <c>Filter(LabelPredicate@col0, Scan(Node,null))</c> → <c>Scan(Node,label)</c>
///   (AllNodesScan→NodeByLabelScan)。graph-first 候補 + Match 由来プランの両方に効く idempotent rule。</item>
/// </list>
/// </remarks>
internal static class LogicalOptimizer
{
    /// <summary>
    /// VEC-10: 構造ヒントが graph-first を示唆していても、label cardinality / TotalNodes が
    /// この値以上なら vector-first にフォールバックする (sidecar 不在 backend で使う legacy 単一閾値)。
    /// </summary>
    internal const double VectorFirstLabelFraction = 0.30;

    /// <summary>
    /// VEC-12: <see cref="GraphStats.HasFastLabelIndex"/> = true 経路で参照する dim → fraction 上限の
    /// 昇順 piecewise table。出典は <c>KnnPushdownThresholdSweepBenchmarks</c> の dim×sel 実測
    /// ([docs/benchmarks/2026-05-20_VEC-12_after.md])。各 dim の crossover に安全マージン 0.05 を引いた値。
    /// </summary>
    private static readonly (int MaxDim, double Threshold)[] FastLabelIndexThresholds =
    {
        (512,          0.30),
        (1024,         0.47),
        (2048,         0.70),
        (int.MaxValue, 0.80),
    };

    /// <summary>
    /// VEC-12: <paramref name="dim"/> に対応する閾値を線形検索で引く。<paramref name="dim"/> &lt;= 0
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

    /// <summary>論理プランを最適化する。<paramref name="stats"/> が null なら KNN は構造ヒントのみで判定。</summary>
    public static LogicalOp Optimize(LogicalOp plan, GraphStats? stats, ISchemaApi schema)
    {
        var p = RewriteKnn(plan, stats, schema);
        p = LabelScanRewrite(p, schema);
        return p;
    }

    // ── KnnLimitPushdown + KnnPushdown (再帰的書き換え) ──────────────────────────

    private static LogicalOp RewriteKnn(LogicalOp n, GraphStats? stats, ISchemaApi schema)
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
        List<Func<ISchemaApi, IPredicate>> filters, KnnOp knn, GraphStats? stats, ISchemaApi schema)
    {
        // 構造ヒントあり (filters 非空)。stats があり label cardinality が閾値以上なら vector-first へ。
        if (stats is not null && ShouldFallBackToVectorFirst(filters, knn.Dim, stats, schema))
            return RebuildStack(filters, knn); // vector-first: filter は Knn(null) の post-filter のまま

        // graph-first: filter を node scan の上に積み直し、label filter を label scan へ畳む。
        LogicalOp candidate = LabelScanRewrite(RebuildStack(filters, new ScanOp(EntityKind.Node, null)), schema);
        return knn with { Candidate = candidate };
    }

    private static bool ShouldFallBackToVectorFirst(
        List<Func<ISchemaApi, IPredicate>> filters, int dim, GraphStats stats, ISchemaApi schema)
    {
        if (stats.TotalNodes <= 0) return false;
        if (FindLabel(filters, schema) is not LabelId lid) return false;
        long card = stats.EstimateCardinality(lid);
        if (card <= 0) return false;

        double fraction = (double)card / stats.TotalNodes;
        double threshold = stats.HasFastLabelIndex ? FastIndexThresholdForDim(dim) : VectorFirstLabelFraction;
        return fraction >= threshold;
    }

    /// <summary>filter 群を materialize し、最初に見つかった col0 の <see cref="LabelPredicate"/> のラベルを返す。</summary>
    private static LabelId? FindLabel(List<Func<ISchemaApi, IPredicate>> filters, ISchemaApi schema)
    {
        foreach (var f in filters)
            if (f(schema) is LabelPredicate { Column: 0 } lp) return lp.Label;
        return null;
    }

    /// <summary>FilterOp を剥がしながら底の <see cref="KnnOp"/> まで辿る。filter は outer→inner 順で返す。</summary>
    private static bool TryCollectKnnStack(LogicalOp n, out List<Func<ISchemaApi, IPredicate>> filters, out KnnOp? knn)
    {
        filters = new List<Func<ISchemaApi, IPredicate>>();
        var cur = n;
        while (cur is FilterOp f) { filters.Add(f.PredicateFactory); cur = f.Source; }
        if (cur is KnnOp k) { knn = k; return true; }
        knn = null;
        return false;
    }

    /// <summary>outer→inner 順の filter 群を <paramref name="baseOp"/> の上に元の入れ子で積み直す。</summary>
    private static LogicalOp RebuildStack(List<Func<ISchemaApi, IPredicate>> filters, LogicalOp baseOp)
    {
        var result = baseOp;
        for (int i = filters.Count - 1; i >= 0; i--)
            result = new FilterOp(result, filters[i]);
        return result;
    }

    // ── LabelScanRewrite ────────────────────────────────────────────────────────

    private static LogicalOp LabelScanRewrite(LogicalOp n, ISchemaApi schema)
    {
        n = RewriteChildren(n, c => LabelScanRewrite(c, schema));
        if (n is FilterOp { Source: ScanOp { Kind: EntityKind.Node, Label: null } } f
            && f.PredicateFactory(schema) is LabelPredicate { Column: 0 } lp)
            return new ScanOp(EntityKind.Node, lp.Label);
        return n;
    }

    // ── 子ノードの汎用書き換え ──────────────────────────────────────────────────

    private static LogicalOp RewriteChildren(LogicalOp n, Func<LogicalOp, LogicalOp> f) => n switch
    {
        FilterOp x               => x with { Source = f(x.Source) },
        ExpandOp x               => x with { Source = f(x.Source) },
        VarLenExpandOp x         => x with { Source = f(x.Source) },
        PathOp x                 => x with { Source = f(x.Source) },
        PropertyLookupOp x       => x with { Source = f(x.Source) },
        LabelNameLookupOp x      => x with { Source = f(x.Source) },
        RelationshipEndpointOp x => x with { Source = f(x.Source) },
        LimitOp x                => x with { Source = f(x.Source) },
        SortOp x                 => x with { Source = f(x.Source) },
        DedupOp x                => x with { Source = f(x.Source) },
        BranchOp x               => x with { Source = f(x.Source) },
        KnnOp x                  => x.Candidate is null ? x : x with { Candidate = f(x.Candidate) },
        FullTextScanOp x         => x.Candidate is null ? x : x with { Candidate = f(x.Candidate) },
        FusionOp x               => x with { Children = ImmutableArray.CreateRange(x.Children, f) },
        _                        => n, // 葉: ScanOp / NodeSeedOp / CorrelatedInputOp
    };
}

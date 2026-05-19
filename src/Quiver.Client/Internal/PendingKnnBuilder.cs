using Quiver.Core;
using Quiver.Operators;

namespace Quiver.Client.Internal;

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

    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal IOperatorBuilder Candidate => _candidate;
    internal string IndexName => _indexName;
    internal ReadOnlySpan<float> Query => _query;
    internal int K => _k;

    /// <summary>
    /// VEC-10: 構造ヒントが graph-first を示唆していても、label cardinality / TotalNodes が
    /// この値以上なら vector-first にフォールバックする。VEC-9 のベンチで graph-first が
    /// 全 sel で wall-clock 劣位 (NodeByLabelScan が N=100k で ~100ms) だったため、
    /// k starvation のリスクが残る低 sel 域は引き続き graph-first を選ぶ保守的閾値。
    /// </summary>
    internal const double VectorFirstLabelFraction = 0.30;

    internal PendingKnnBuilder(IOperatorBuilder candidate, string indexName, ReadOnlySpan<float> query, int k)
    {
        _candidate = candidate;
        _indexName = indexName;
        _query = query.ToArray();
        _k = k;
    }

    /// <summary>同じ <see cref="_query"/> 配列を共有しつつ candidate を差し替えた新 PendingKnn。</summary>
    internal PendingKnnBuilder WithCandidate(IOperatorBuilder candidate)
        => new(candidate, _indexName, _query, _k, sharedQuery: true);

    /// <summary>K を差し替えた新 PendingKnn (Limit shrink 用)。</summary>
    internal PendingKnnBuilder WithK(int newK)
        => new(_candidate, _indexName, _query, newK, sharedQuery: true);

    // 配列を再コピーしない private ctor。WithCandidate / WithK 経由でのみ呼ばれる。
    private PendingKnnBuilder(IOperatorBuilder candidate, string indexName, float[] query, int k, bool sharedQuery)
    {
        _candidate = candidate;
        _indexName = indexName;
        _query = query;
        _k = k;
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
    /// VEC-10: candidate チェーンから「最内 <see cref="ScanBuilder"/> の Label」を取り出し、
    /// その label cardinality / TotalNodes が <see cref="VectorFirstLabelFraction"/> 以上なら true。
    /// label が抽出できない (Has のみで構成された FilterBuilder チェーン等) ときは
    /// 判断材料が不足するため graph-first を維持する (false を返す)。
    /// </summary>
    private static bool ShouldFallBackToVectorFirst(IOperatorBuilder candidate, GraphStats stats, ISchemaApi schema)
    {
        string? label = FindInnermostScanLabel(candidate);
        if (label is null) return false;
        if (stats.TotalNodes <= 0) return false;

        var lid = schema.GetOrCreateLabel(label);
        long card = stats.EstimateCardinality(lid);
        if (card <= 0) return false;

        double fraction = (double)card / stats.TotalNodes;
        return fraction >= VectorFirstLabelFraction;
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

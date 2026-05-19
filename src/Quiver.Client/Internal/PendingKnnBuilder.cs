using Quiver.Operators;

namespace Quiver.Client.Internal;

/// <summary>
/// VEC-9: <c>g.Knn(...)</c> 直後の遅延 builder。後続が pure-filter (HasLabel/Has/Where/...) や
/// <c>Limit</c> なら candidate-side / k を更新した新インスタンスに置き換え、non-pure step や
/// terminal で <see cref="Materialize"/> によって <see cref="KnnNodeSourceBuilder"/> (vector-first)
/// または <see cref="FilteredKnnNodeSourceBuilder"/> (graph-first) に確定する。
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
    /// candidate の構造ヒントで vector-first / graph-first を選択する。
    /// FilterBuilder で囲われている、または ScanBuilder が label 指定済みなら絞り込み済とみなし graph-first。
    /// 素の ScanBuilder() (AllNodes) のままなら現状動作 = vector-first。
    /// </summary>
    internal IOperatorBuilder Materialize()
        => HasStructuralSelectivityHint(_candidate)
            ? new FilteredKnnNodeSourceBuilder(_candidate, _indexName, _query, _k)
            : new KnnNodeSourceBuilder(_indexName, _query, _k);

    public IPhysicalOperator Build(ISchemaApi schema) => Materialize().Build(schema);

    private static bool HasStructuralSelectivityHint(IOperatorBuilder b)
        => b switch
        {
            FilterBuilder => true,
            ScanBuilder s when s.Label is not null => true,
            _ => false,
        };
}

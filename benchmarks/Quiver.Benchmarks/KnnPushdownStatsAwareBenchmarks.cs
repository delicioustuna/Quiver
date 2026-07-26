using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Api.Internal;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// statistics-aware push-down fallback の wall-clock 効果計測。
/// 同じ <c>g.Knn(idx, q, K).HasLabel("Hit")</c> パターンを 3 ルートで比較する:
/// <list type="number">
///   <item><c>PostFilter</c> — 旧 vector-first (KNN top-K → label post-filter の物理プランを直接構築)。baseline。</item>
///   <item><c>PushdownVec9</c> — graph-first push-down (stats 不注入、構造ヒントのみ)。</item>
///   <item><c>PushdownVec10</c> — stats-aware (label cardinality &gt;= 30% で
///   vector-first にフォールバック)。</item>
/// </list>
/// 期待形状: sel &lt; 30% では  ≈  (どちらも graph-first を選ぶ)、
/// sel &gt;= 30% では  ≈ PostFilter (fallback で wall-clock 劣化を回避)。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class KnnPushdownStatsAwareBenchmarks
{
    [Params(100_000)]
    public int N { get; set; }

    // Hit fraction (× 1000). 10 = 1%, 50 = 5%, 250 = 25% はgraph-first選択域、
    // 400 = 40%, 600 = 60% はvector-first fallback発火域。
    [Params(10, 50, 250, 400, 600)]
    public int FractionPermille { get; set; }

    private const int Dim = 768;
    private const int K = 10;
    private const string IndexName = "vec10-bench";

    private string _dir = null!;
    private QuiverDatabase _db = null!;
    private GraphStats _stats = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(2026);
        _dir = BenchTempDir.Create("vec10");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        _db.EditSchema(schema =>
        {
            schema.GetOrCreatePropertyKey("embedding");
            schema.CreateIndex(new VectorIndexDefinition(
                IndexName,
                new PropertyTarget(PropertyOwnerKind.Vertex, "embedding"),
                Dim));
        });

        int hitCount = Math.Max(1, (int)((long)N * FractionPermille / 1000));
        var hitSet = new HashSet<int>();
        while (hitSet.Count < hitCount) hitSet.Add(rng.Next(N));

        var buf = new float[Dim];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                var n = tx.CreateVertex(hitSet.Contains(i) ? "Hit" : "Miss");
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                tx.SetVectorProperty(EntityRef.From(n), "embedding", buf);
            }
            tx.Commit();
        }

        _stats = _db.CollectStats();

        _query = new float[Dim];
        for (int d = 0; d < Dim; d++) _query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dir);
    }

    /// <summary>
    /// 旧 vector-first baseline: top-K を全 N から取得後、HasLabel で post-filter。
    /// KNN top-K → label post-filter の物理プランを直接構築する (: optimizer を介さない)。
    /// </summary>
    [Benchmark(Baseline = true)]
    public int PostFilter()
    {
        using var rtx = _db.BeginReadTransaction();
        return KnnBenchSupport.PostFilterCount(rtx, _db.Schema, IndexName, _query, K, "Hit");
    }

    /// <summary> default: stats 不注入で構造ヒントのみで graph-first を選ぶ。</summary>
    [Benchmark]
    public int PushdownVec9()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var result = g.Knn(IndexName, _query, K).HasLabel("Hit").ToList();
        return result.Count;
    }

    /// <summary>
    /// stats を渡して label cardinality &gt;= 30% で vector-first にフォールバックさせる。
    /// </summary>
    [Benchmark]
    public int PushdownVec10()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query.WithStats(_stats);
        var result = g.Knn(IndexName, _query, K).HasLabel("Hit").ToList();
        return result.Count;
    }
}

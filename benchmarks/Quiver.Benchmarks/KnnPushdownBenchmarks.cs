using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Api.Internal;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// VEC-9: post-filter (vector-first) vs push-down (graph-first) for the
/// <c>g.Knn(idx, q, K).HasLabel("Hit").ToList()</c> pattern. The push-down
/// path is the default behavior after VEC-9. The post-filter baseline is
/// reproduced by constructing a <see cref="KnnNodeSourceBuilder"/>-based
/// traversal directly (bypassing the <see cref="PendingKnnBuilder"/> rewrite)
/// and applying <c>.HasLabel("Hit")</c> as a downstream FilterBuilder.
///
/// Expected speedup (from VEC-8 extrapolation): 0.1% sel ~100×, 1% ~30-50×,
/// 5% ~10-20×, 25% ~2-3×.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class KnnPushdownBenchmarks
{
    [Params(100_000)]
    public int N { get; set; }

    // Hit fraction (× 1000). 1 ≈ 0.1%, 10 ≈ 1%, 50 ≈ 5%, 250 ≈ 25%.
    [Params(1, 10, 50, 250)]
    public int FractionPermille { get; set; }

    private const int Dim = 768;
    private const int K = 10;
    private const string IndexName = "pushdown-bench";

    private string _dir = null!;
    private GraphDatabase _db = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(2026);
        _dir = BenchTempDir.Create("vec9");
        _db = GraphDatabase.Open(_dir);

        var keyId = _db.Schema.GetOrCreatePropertyKey("title");
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, keyId, Dim,
            DistanceMetric.Cosine, "bench", null));

        int hitCount = Math.Max(1, (int)((long)N * FractionPermille / 1000));
        var hitSet = new HashSet<int>();
        while (hitSet.Count < hitCount) hitSet.Add(rng.Next(N));

        var buf = new float[Dim];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                var n = tx.CreateNode(hitSet.Contains(i) ? "Hit" : "Miss");
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                _db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, buf);
            }
            tx.Commit();
        }

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
    /// Legacy post-filter: top-K from full N, then drop by label. Construct
    /// the traversal with <see cref="KnnNodeSourceBuilder"/> directly so the
    /// PendingKnn rewrite does not kick in; subsequent <c>HasLabel</c> on a
    /// non-PendingKnn / non-ScanBuilder source becomes a FilterBuilder
    /// (= the VEC-9 pre-rewrite plan).
    /// </summary>
    [Benchmark(Baseline = true)]
    public int PostFilter()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var knnBuilder = new KnnNodeSourceBuilder(IndexName, _query, K);
        var traversal = new GraphTraversal<NodeId>(rtx, _db.Schema, knnBuilder, row => row.GetNodeId(0), 0);
        var result = traversal.HasLabel("Hit").ToList();
        return result.Count;
    }

    /// <summary>VEC-9 default: <c>g.Knn().HasLabel()</c> is rewritten to graph-first.</summary>
    [Benchmark]
    public int Pushdown()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var result = g.Knn(IndexName, _query, K).HasLabel("Hit").ToList();
        return result.Count;
    }
}

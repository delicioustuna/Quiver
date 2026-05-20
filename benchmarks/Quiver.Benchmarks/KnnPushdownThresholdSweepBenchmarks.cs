using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Client;
using Quiver.Client.Internal;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// VEC-12: PendingKnnBuilder の vector-first フォールバック閾値を dim × sel の 2 軸で実測する
/// クロスオーバー sweep。VEC-11 で <c>NodeByLabelScan</c> が O(N) → O(|L|) になり、
/// VEC-10 の 0.30 一本の閾値が dim ごとに異なる crossover を表現できなくなったための再評価用。
/// <para>
/// 各 (dim, sel) で次の 3 メソッドを計測:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>PostFilter</c> — baseline。<c>KnnNodeSourceBuilder</c> を直接構築して
///     <see cref="PendingKnnBuilder"/> rewrite を bypass し、後段に <c>HasLabel</c> FilterBuilder を被せる
///     (vector-first 直結, post-filter)。
///   </item>
///   <item>
///     <c>GraphFirstForced</c> — VEC-11 後の graph-first 経路を強制。stats 不注入で構造ヒントのみで
///     <see cref="FilteredKnnNodeSourceBuilder"/> (graph-first) を選ばせる。
///   </item>
///   <item>
///     <c>VectorFirstForced</c> — VEC-10 fallback と同等の plan を強制。<c>KnnNodeSourceBuilder</c>
///     を直接構築して PendingKnn rewrite を bypass、後段に <c>LabelPredicate</c> を被せる。
///   </item>
/// </list>
/// <para>
/// 各 dim ごとに <c>GraphFirstForced.Mean == VectorFirstForced.Mean</c> となる sel を線形補間で求め、
/// 安全マージン 0.05 を引いた値を <see cref="PendingKnnBuilder"/> の dim-aware piecewise table に採用する。
/// </para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class KnnPushdownThresholdSweepBenchmarks
{
    // 5 dim × 8 sel × 3 method × 3 iter × ~15 ms ≈ 5-10 分の bench 実行を想定。
    // dim は LLM 埋め込みの実用域 (MiniLM=384 / ada-002=768 / text-embedding-3-small=1536 / 3-large=3072)
    // のうちカバレッジを取った 5 点。128 は下界、3072 は上界。
    [Params(128, 384, 768, 1536, 3072)]
    public int Dim { get; set; }

    // Hit fraction (× 1000)。crossover 探索域 30-80% を 8 点で sweep。
    [Params(300, 400, 450, 500, 550, 600, 700, 800)]
    public int FractionPermille { get; set; }

    private const int N = 100_000;
    private const int K = 10;
    private const string IndexName = "vec12-sweep";

    private string _dir = null!;
    private GraphDatabase _db = null!;
    private GraphStats _stats = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(2026);
        _dir = Path.Combine(Path.GetTempPath(), "quiver_bench_vec12_" + Guid.NewGuid().ToString("N"));
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

        _stats = _db.CollectStats();

        _query = new float[Dim];
        for (int d = 0; d < Dim; d++) _query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// post-filter baseline。<see cref="KnnNodeSourceBuilder"/> を直接構築して
    /// PendingKnn rewrite を bypass、後段に <c>HasLabel("Hit")</c> FilterBuilder を被せる。
    /// </summary>
    [Benchmark(Baseline = true)]
    public int PostFilter()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var knnBuilder = new KnnNodeSourceBuilder(IndexName, _query, K);
        var traversal = new GraphTraversal<NodeId>(rtx, _db.Schema, knnBuilder, row => row.GetNodeId(0), 0);
        return traversal.HasLabel("Hit").ToList().Count;
    }

    /// <summary>
    /// VEC-11 後の graph-first 経路 (VEC-9 default)。stats 不注入で構造ヒントのみで
    /// <see cref="FilteredKnnNodeSourceBuilder"/> を選ぶ。LabelNodeIndex sidecar が接続されていれば
    /// <c>NodeByLabelScan</c> は O(|L|)。
    /// </summary>
    [Benchmark]
    public int GraphFirstForced()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        return g.Knn(IndexName, _query, K).HasLabel("Hit").ToList().Count;
    }

    /// <summary>
    /// VEC-10 fallback 経路と同等の plan を強制。<see cref="KnnNodeSourceBuilder"/> を直接構築して
    /// PendingKnn rewrite を bypass、ScanBuilder("Hit") のラベル指定を post-filter
    /// (<c>LabelPredicate</c>) として再配置する形を模倣。
    /// </summary>
    [Benchmark]
    public int VectorFirstForced()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var knnBuilder = new KnnNodeSourceBuilder(IndexName, _query, K);
        var traversal = new GraphTraversal<NodeId>(rtx, _db.Schema, knnBuilder, row => row.GetNodeId(0), 0);
        return traversal.HasLabel("Hit").ToList().Count;
    }
}

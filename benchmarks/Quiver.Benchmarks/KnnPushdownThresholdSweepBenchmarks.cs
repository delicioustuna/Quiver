using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Api.Internal;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// vector-first フォールバック閾値を dim × sel の 2 軸で実測するクロスオーバー sweep。
/// <c>VertexByLabelScan</c> が O(N) → O(|L|) になったため、単一の0.30閾値が
/// dim ごとに異なる crossover を表現できなくなったための再評価用。
/// <para>
/// 各 (dim, sel) で次の 3 メソッドを計測:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>PostFilter</c> — baseline。KNN top-K → label post-filter の物理プランを直接構築
///     (vector-first 直結)。
///   </item>
///   <item>
///     <c>GraphFirstForced</c> — graph-first経路を強制。stats 不注入で構造ヒントのみで
///     graph-first (FilteredKnn) を選ばせる。
///   </item>
///   <item>
///     <c>VectorFirstForced</c> — vector-first fallbackと同等のplanを強制。post-filter物理プランを直接構築。
///   </item>
/// </list>
/// <para>
/// 各 dim ごとに <c>GraphFirstForced.Mean == VectorFirstForced.Mean</c> となる sel を線形補間で求め、
/// 安全マージン 0.05 を引いた値を <c>LogicalOptimizer</c> の dim-aware piecewise table に採用する。
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
    private QuiverDatabase _db = null!;
    private GraphStats _stats = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(2026);
        _dir = BenchTempDir.Create("vec12");
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
    /// post-filter baseline。KNN top-K → label post-filter の物理プランを直接構築する
    /// (: optimizer を介さない vector-first 基準)。
    /// </summary>
    [Benchmark(Baseline = true)]
    public int PostFilter()
    {
        using var rtx = _db.BeginReadTransaction();
        return KnnBenchSupport.PostFilterCount(rtx, _db.Schema, IndexName, _query, K, "Hit");
    }

    /// <summary>
    /// graph-first経路。stats 不注入で構造ヒントのみで
    /// <see cref="FilteredKnnVertexSourceBuilder"/> を選ぶ。LabelVertexIndex sidecar が接続されていれば
    /// <c>VertexByLabelScan</c> は O(|L|)。
    /// </summary>
    [Benchmark]
    public int GraphFirstForced()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        return g.Knn(IndexName, _query, K).HasLabel("Hit").ToList().Count;
    }

    /// <summary>
    /// vector-first fallback経路と同等のplanを強制。KNN top-K → label post-filterの物理プランを
    /// 直接構築する (= PostFilter と同形、vector-first)。
    /// </summary>
    [Benchmark]
    public int VectorFirstForced()
    {
        using var rtx = _db.BeginReadTransaction();
        return KnnBenchSupport.PostFilterCount(rtx, _db.Schema, IndexName, _query, K, "Hit");
    }
}

using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// ARCH-6 (6d): persistent HNSW ANN search vs brute-force flat scan over the same
/// in-file corpus, exercised through the public <see cref="GraphDatabase"/> surface
/// (so the persistent <c>PersistentVectorStore</c> + <c>HnswIndex</c> path is measured,
/// not the in-memory reference store).
///
/// <para><c>HnswSearch</c> = <see cref="Core.IVectorStore.KnnSearch"/> (HNSW graph).
/// <c>FlatScan</c> = <see cref="Core.IVectorStore.KnnSearchBatch"/> with a single query
/// (full-corpus scan). HNSW should be markedly faster as N grows while keeping high
/// recall (verified in VectorHnswTests).</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class HnswSearchBenchmarks
{
    [Params(10_000)]
    public int N { get; set; }

    private const int Dim = 128;
    private const int K = 10;
    private const string IndexName = "hnsw-bench";

    private string _dir = null!;
    private GraphDatabase _db = null!;
    private float[] _query = null!;
    private ReadOnlyMemory<float>[] _batchQuery = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(98765);
        _dir = BenchTempDir.Create("hnsw");
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, _db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Cosine, "bench"));

        var buf = new float[Dim];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                var n = tx.CreateNode("Doc");
                _db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, buf);
            }
            tx.Commit();
        }

        _query = new float[Dim];
        for (int d = 0; d < Dim; d++) _query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
        _batchQuery = new[] { (ReadOnlyMemory<float>)_query.AsMemory() };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        BenchTempDir.Delete(_dir);
    }

    [Benchmark(Baseline = true)]
    public float FlatScan()
    {
        float acc = 0f;
        using var c = _db.Vectors.KnnSearchBatch(IndexName, _batchQuery, K)[0];
        while (c.MoveNext()) acc += c.Current.Score;
        return acc;
    }

    [Benchmark]
    public float HnswSearch()
    {
        float acc = 0f;
        using var c = _db.Vectors.KnnSearch(IndexName, _query, K);
        while (c.MoveNext()) acc += c.Current.Score;
        return acc;
    }
}

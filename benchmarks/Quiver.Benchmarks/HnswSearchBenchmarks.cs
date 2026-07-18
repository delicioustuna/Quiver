using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Testing;

namespace Quiver.Benchmarks;

/// <summary>
/// Persistent HNSW ANN search vs brute-force flat scan over the same
/// in-file corpus, exercised through the public <see cref="QuiverDatabase"/> surface
/// (so the persistent <c>PersistentVectorStore</c> + <c>HnswIndex</c> path is measured,
/// not the in-memory reference store).
///
/// <para><c>HnswSearch</c> = <see cref="Core.IVectorStore.KnnSearch"/> (HNSW graph)。
/// <c>ExactFlatScan</c> は persistent payload を直接全走査する内部 baseline であり、
/// HNSW を一切経由しない。HNSW should be markedly faster as N grows while keeping high
/// recall (verified by the RecallCheck recall gate).</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class HnswSearchBenchmarks
{
    [Params(10_000)]
    public int N { get; set; }

    [Params(384, 768)]
    public int Dim { get; set; }

    private const int K = 10;
    private const string IndexName = "hnsw-bench";

    private string _dir = null!;
    private QuiverDatabase _db = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(VectorRecallCorpus.Seed);
        _dir = BenchTempDir.Create("hnsw");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Vertex, _db.EditSchema(schema => schema.GetOrCreatePropertyKey("t")),
            Dim, DistanceMetric.Cosine, "bench"));

        var buf = new float[Dim];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                VectorRecallCorpus.Fill(rng, buf);
                var n = tx.CreateVertex("Doc");
                _db.Vectors.SetVector(EntityKind.Vertex, n.Value, IndexName, buf);
            }
            tx.Commit();
        }

        _query = new float[Dim];
        VectorRecallCorpus.Fill(rng, _query);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        BenchTempDir.Delete(_dir);
    }

    [Benchmark(Baseline = true)]
    public float ExactFlatScan()
    {
        float acc = 0f;
        var store = (AutocommitVectorStore)_db.Vectors;
        using var c = store.KnnSearchExact(IndexName, _query, K);
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

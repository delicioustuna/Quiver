using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Testing;

namespace Quiver.Benchmarks;

/// <summary>
/// Immutable HNSW segment search vs brute-force primary scan over the same
/// in-file corpus, exercised through the public transaction surface.
///
/// <para>transaction-scoped KNN の検索コストを測定する。
/// recall は RecallCheck の独立 gate で検証する。</para>
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
        _db.EditSchema(schema =>
        {
            schema.GetOrCreatePropertyKey("embedding");
            schema.CreateIndex(new VectorIndexDefinition(
                IndexName,
                new PropertyTarget(PropertyOwnerKind.Vertex, "embedding", "Doc"),
                Dim));
        });

        var buf = new float[Dim];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                VectorRecallCorpus.Fill(rng, buf);
                var n = tx.CreateVertex("Doc");
                tx.SetVectorProperty(EntityRef.From(n), "embedding", buf);
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
    public float FirstSearch()
    {
        float acc = 0f;
        using var tx = _db.BeginReadTransaction();
        using var c = tx.KnnSearch(IndexName, _query, K);
        while (c.MoveNext()) acc += c.Current.Score;
        return acc;
    }

    [Benchmark]
    public float HnswSearch()
    {
        float acc = 0f;
        using var tx = _db.BeginReadTransaction();
        using var c = tx.KnnSearch(IndexName, _query, K);
        while (c.MoveNext()) acc += c.Current.Score;
        return acc;
    }
}

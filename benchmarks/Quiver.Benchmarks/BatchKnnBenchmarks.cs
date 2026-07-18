using BenchmarkDotNet.Attributes;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// per-query loop vs transaction-scoped <see cref="IReadTransaction.KnnSearchBatch"/>.
/// Sweeps Q (concurrent query count) over a fixed dim=768, N=100k corpus. With
/// Q increasing, per-query cost should drop because the snapshot is taken once
/// and the corpus is streamed once.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BatchKnnBenchmarks
{
    [Params(1, 8, 64, 256)]
    public int Q { get; set; }

    private const int Dim = 768;
    private const int N = 100_000;
    private const int K = 10;
    private const string IndexName = "batch-bench";

    private string _dir = null!;
    private QuiverDatabase _database = null!;
    private IReadTransaction _read = null!;
    private ReadOnlyMemory<float>[] _queries = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(31337);
        _dir = BenchTempDir.Create("batch_knn");
        _database = QuiverDatabase.CreateInMemory();
        _database.EditSchema(schema =>
        {
            schema.GetOrCreatePropertyKey("embedding");
            schema.CreateIndex(new VectorIndexDefinition(
                IndexName,
                new PropertyTarget(PropertyOwnerKind.Vertex, "embedding", "Doc"),
                Dim));
        });

        var buf = new float[Dim];
        using (var write = _database.BeginWriteTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                for (int d = 0; d < Dim; d++)
                    buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                VertexId owner = write.CreateVertex("Doc");
                write.SetVectorProperty(EntityRef.From(owner), "embedding", buf);
            }
            write.Commit();
        }

        _queries = new ReadOnlyMemory<float>[Q];
        for (int q = 0; q < Q; q++)
        {
            var v = new float[Dim];
            for (int d = 0; d < Dim; d++) v[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
            _queries[q] = v;
        }
        _read = _database.BeginReadTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _read.Dispose();
        _database.Dispose();
        BenchTempDir.Delete(_dir);
    }

    [Benchmark(Baseline = true)]
    public float PerQueryLoop()
    {
        float acc = 0f;
        for (int i = 0; i < Q; i++)
        {
            using var cursor = _read.KnnSearch(IndexName, _queries[i].Span, K);
            while (cursor.MoveNext()) acc += cursor.Current.Score;
        }
        return acc;
    }

    [Benchmark]
    public float Batch()
    {
        float acc = 0f;
        var cursors = _read.KnnSearchBatch(IndexName, _queries, K);
        for (int i = 0; i < Q; i++)
        {
            using var c = cursors[i];
            while (c.MoveNext()) acc += c.Current.Score;
        }
        return acc;
    }
}

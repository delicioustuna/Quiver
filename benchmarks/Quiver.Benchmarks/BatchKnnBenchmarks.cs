using BenchmarkDotNet.Attributes;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// per-query loop vs single-snapshot <see cref="InMemoryVectorStore.KnnSearchBatch"/>.
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

    private InMemoryVectorStore _store = null!;
    private ReadOnlyMemory<float>[] _queries = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(31337);
        _store = new InMemoryVectorStore();
        _store.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Vertex, new PropertyKeyId(1), Dim,
            DistanceMetric.Cosine, "bench"));

        var buf = new float[Dim];
        for (int i = 0; i < N; i++)
        {
            for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
            _store.SetVector(EntityKind.Vertex, i, IndexName, buf);
        }

        _queries = new ReadOnlyMemory<float>[Q];
        for (int q = 0; q < Q; q++)
        {
            var v = new float[Dim];
            for (int d = 0; d < Dim; d++) v[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
            _queries[q] = v;
        }
    }

    [Benchmark(Baseline = true)]
    public float PerQueryLoop()
    {
        float acc = 0f;
        for (int i = 0; i < Q; i++)
        {
            using var cursor = _store.KnnSearch(IndexName, _queries[i].Span, K);
            while (cursor.MoveNext()) acc += cursor.Current.Score;
        }
        return acc;
    }

    [Benchmark]
    public float Batch()
    {
        float acc = 0f;
        var cursors = _store.KnnSearchBatch(IndexName, _queries, K);
        for (int i = 0; i < Q; i++)
        {
            using var c = cursors[i];
            while (c.MoveNext()) acc += c.Current.Score;
        }
        return acc;
    }
}

using BenchmarkDotNet.Attributes;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// SIMD (<see cref="VectorScorer"/>) vs scalar (<see cref="ScalarVectorScorer"/>)
/// per-pair distance/similarity throughput.
///
/// Sweeps dim ∈ {128, 768, 1536} × N ∈ {1k, 10k, 100k} × metric. The benchmark
/// runs N pair-scoring operations against a fixed query so the wall-clock
/// reflects steady-state scoring cost (the dominant inner loop of
/// <see cref="InMemoryVectorStore.KnnSearch"/>).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class VectorScoreBenchmarks
{
    [Params(128, 768, 1536)]
    public int Dim { get; set; }

    [Params(1_000, 10_000, 100_000)]
    public int N { get; set; }

    [Params(DistanceMetric.Cosine, DistanceMetric.Dot, DistanceMetric.Euclidean)]
    public DistanceMetric Metric { get; set; }

    private float[] _query = null!;
    private float[][] _corpus = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(1234);
        _query = new float[Dim];
        for (int i = 0; i < Dim; i++) _query[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        _corpus = new float[N][];
        for (int j = 0; j < N; j++)
        {
            var v = new float[Dim];
            for (int i = 0; i < Dim; i++) v[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            _corpus[j] = v;
        }
    }

    [Benchmark(Baseline = true)]
    public float Scalar()
    {
        float acc = 0f;
        switch (Metric)
        {
            case DistanceMetric.Cosine:
                for (int j = 0; j < _corpus.Length; j++)
                    acc += ScalarVectorScorer.Cosine(_query, _corpus[j]);
                break;
            case DistanceMetric.Dot:
                for (int j = 0; j < _corpus.Length; j++)
                    acc += ScalarVectorScorer.Dot(_query, _corpus[j]);
                break;
            case DistanceMetric.Euclidean:
                for (int j = 0; j < _corpus.Length; j++)
                    acc += ScalarVectorScorer.Euclidean(_query, _corpus[j]);
                break;
        }
        return acc;
    }

    [Benchmark]
    public float Simd()
    {
        float acc = 0f;
        switch (Metric)
        {
            case DistanceMetric.Cosine:
                for (int j = 0; j < _corpus.Length; j++)
                    acc += VectorScorer.Cosine(_query, _corpus[j]);
                break;
            case DistanceMetric.Dot:
                for (int j = 0; j < _corpus.Length; j++)
                    acc += VectorScorer.Dot(_query, _corpus[j]);
                break;
            case DistanceMetric.Euclidean:
                for (int j = 0; j < _corpus.Length; j++)
                    acc += VectorScorer.Euclidean(_query, _corpus[j]);
                break;
        }
        return acc;
    }
}

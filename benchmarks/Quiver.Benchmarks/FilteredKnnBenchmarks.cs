using BenchmarkDotNet.Attributes;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// VEC-8: gather-then-score vs default oversample-and-filter for
/// <see cref="InMemoryVectorStore.KnnSearchFiltered"/>. Sweeps N (index size)
/// against the candidate-set fraction; gather should dominate when the candidate
/// set is sparse and converge with the oversample path as it grows.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FilteredKnnBenchmarks
{
    [Params(10_000, 100_000)]
    public int N { get; set; }

    // Candidate fraction (× 1000). 1 ≈ 0.1%, 10 ≈ 1%, 50 ≈ 5%, 250 ≈ 25%.
    [Params(1, 10, 50, 250)]
    public int FractionPermille { get; set; }

    private const int Dim = 768;
    private const int K = 10;

    private InMemoryVectorStore _store = null!;
    private float[] _query = null!;
    private EntityCandidateSet _candidates = null!;
    private const string IndexName = "filtered-bench";

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(2026);
        _store = new InMemoryVectorStore();
        _store.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, new PropertyKeyId(1), Dim,
            DistanceMetric.Cosine, "bench"));

        var buf = new float[Dim];
        for (int i = 0; i < N; i++)
        {
            for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
            _store.SetVector(EntityKind.Node, i, IndexName, buf);
        }

        _query = new float[Dim];
        for (int d = 0; d < Dim; d++) _query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);

        int candidateCount = Math.Max(1, (int)((long)N * FractionPermille / 1000));
        var ids = new long[candidateCount];
        // Random distinct ids in [0, N).
        var picked = new HashSet<long>();
        while (picked.Count < candidateCount) picked.Add(rng.Next(N));
        int idx = 0;
        foreach (var id in picked) ids[idx++] = id;
        _candidates = new EntityCandidateSet(EntityKind.Node, ids);
    }

    /// <summary>Default oversample-and-filter: invoke the interface helper directly.</summary>
    [Benchmark(Baseline = true)]
    public float Oversample()
    {
        // Replicate IGraphAccessMethods.KnnSearchFilteredOversample using public KnnSearch.
        int candidateCount = _candidates.Count;
        int oversampleCap = Math.Max(K * 64, candidateCount * 2);
        int candidateK = Math.Min(Math.Max(K * 4, K + candidateCount / 4), oversampleCap);

        float acc = 0f;
        while (true)
        {
            var hits = new List<VectorSearchResult>(K);
            using (var cursor = _store.KnnSearch(IndexName, _query, candidateK))
            {
                while (cursor.MoveNext())
                {
                    var hit = cursor.Current;
                    if (!_candidates.Contains(hit.EntityKind, hit.EntityId)) continue;
                    hits.Add(hit);
                    if (hits.Count >= K) break;
                }
            }

            if (hits.Count >= K || candidateK >= oversampleCap)
            {
                foreach (var h in hits) acc += h.Score;
                return acc;
            }
            candidateK = Math.Min(candidateK * 2, oversampleCap);
        }
    }

    /// <summary>VEC-8 gather-then-score.</summary>
    [Benchmark]
    public float Gather()
    {
        float acc = 0f;
        using var cursor = _store.KnnSearchFiltered(IndexName, _query, K, _candidates);
        while (cursor.MoveNext()) acc += cursor.Current.Score;
        return acc;
    }
}

using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <see cref="InMemoryVectorStore.KnnSearchFiltered"/> の候補収集後採点と、
/// <see cref="InMemoryVectorStore.KnnSearchBatch"/> の単一スナップショット Q×N 処理を検証する。
/// 既定の候補追加取得後フィルター経路、およびクエリ単位の
/// <see cref="InMemoryVectorStore.KnnSearch"/> と結果が一致することを確認する。
/// </summary>
public sealed class InMemoryVectorStoreBatchTests
{
    private const string IndexName = "batch-embed";

    private static VectorIndexSpec Spec(int dim, DistanceMetric metric)
        => new(IndexName, EntityKind.Vertex, new PropertyKeyId(1), dim, metric, "test");

    [Theory]
    [InlineData(DistanceMetric.Cosine)]
    [InlineData(DistanceMetric.Dot)]
    [InlineData(DistanceMetric.Euclidean)]
    public void KnnSearchFiltered_gather_matches_full_scan_filter(DistanceMetric metric)
    {
        const int dim = 16;
        const int n = 2_000;
        const int candidates = 100; // ~5% — well below the gather threshold (N/4).
        const int k = 10;

        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(Spec(dim, metric));

        var rng = new Random(7);
        for (int i = 0; i < n; i++)
            store.SetVector(EntityKind.Vertex, i, IndexName, RandomVector(rng, dim));

        // Take every 20th id so the candidate set is sparse and the gather path
        // is the natural pick.
        var candidateIds = new long[candidates];
        for (int i = 0; i < candidates; i++) candidateIds[i] = i * (n / candidates);
        var set = new EntityCandidateSet(EntityKind.Vertex, candidateIds);

        var query = RandomVector(rng, dim);

        // Reference: top-k from a full-corpus KnnSearch, then post-filter by candidate set.
        var expected = new List<(long Id, float Score)>();
        using (var c = store.KnnSearch(IndexName, query, k: n))
        {
            while (c.MoveNext())
            {
                var hit = c.Current;
                if (set.Contains(hit.EntityKind, hit.EntityId))
                    expected.Add((hit.EntityId, hit.Score));
                if (expected.Count == k) break;
            }
        }

        using var actual = store.KnnSearchFiltered(IndexName, query, k, set);
        var actualList = Drain(actual);

        actualList.Should().HaveCount(k);
        for (int rank = 0; rank < k; rank++)
        {
            actualList[rank].Id.Should().Be(expected[rank].Id);
            actualList[rank].Score.Should().BeApproximately(expected[rank].Score, 1e-4f);
        }
    }

    [Fact]
    public void KnnSearchFiltered_falls_back_to_scan_when_candidates_are_dense()
    {
        // candidates > N/4 forces the scan path inside InMemoryVectorStore.
        const int dim = 8;
        const int n = 200;
        const int k = 5;

        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(Spec(dim, DistanceMetric.Dot));

        var rng = new Random(11);
        for (int i = 0; i < n; i++)
            store.SetVector(EntityKind.Vertex, i, IndexName, RandomVector(rng, dim));

        // ~75% of the index → dense path.
        var dense = Enumerable.Range(0, n).Where(i => i % 4 != 0).Select(i => (long)i).ToArray();
        var set = new EntityCandidateSet(EntityKind.Vertex, dense);
        var query = RandomVector(rng, dim);

        using var actual = store.KnnSearchFiltered(IndexName, query, k, set);
        var actualList = Drain(actual);

        actualList.Should().HaveCount(k);
        actualList.Should().OnlyContain(r => set.Contains(EntityKind.Vertex, r.Id));
        for (int i = 1; i < actualList.Count; i++)
            actualList[i].Score.Should().BeLessThanOrEqualTo(actualList[i - 1].Score);
    }

    [Fact]
    public void KnnSearchFiltered_returns_empty_when_candidates_are_empty()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(Spec(3, DistanceMetric.Cosine));
        store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 1, 0, 0 });

        var empty = new EntityCandidateSet(EntityKind.Vertex, Array.Empty<long>());

        using var c = store.KnnSearchFiltered(IndexName, new float[] { 1, 0, 0 }, 5, empty);
        Drain(c).Should().BeEmpty();
    }

    [Fact]
    public void KnnSearchFiltered_returns_empty_when_kind_mismatches()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(Spec(3, DistanceMetric.Cosine));
        store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 1, 0, 0 });

        // Index is Vertex-bound but candidate set declares Edge.
        var wrongKind = new EntityCandidateSet(EntityKind.Edge, new long[] { 1 });

        using var c = store.KnnSearchFiltered(IndexName, new float[] { 1, 0, 0 }, 5, wrongKind);
        Drain(c).Should().BeEmpty();
    }

    [Theory]
    [InlineData(DistanceMetric.Cosine)]
    [InlineData(DistanceMetric.Dot)]
    [InlineData(DistanceMetric.Euclidean)]
    public void KnnSearchBatch_matches_per_query_KnnSearch(DistanceMetric metric)
    {
        const int dim = 8;
        const int n = 500;
        const int k = 7;
        const int q = 16;

        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(Spec(dim, metric));

        var rng = new Random(99);
        for (int i = 0; i < n; i++)
            store.SetVector(EntityKind.Vertex, i, IndexName, RandomVector(rng, dim));

        var queries = new ReadOnlyMemory<float>[q];
        for (int i = 0; i < q; i++) queries[i] = RandomVector(rng, dim);

        var batch = store.KnnSearchBatch(IndexName, queries, k);
        batch.Should().HaveCount(q);

        for (int i = 0; i < q; i++)
        {
            using var refCursor = store.KnnSearch(IndexName, queries[i].Span, k);
            var expected = Drain(refCursor);
            var actual = Drain(batch[i]);
            actual.Should().HaveCount(expected.Count);
            for (int rank = 0; rank < expected.Count; rank++)
            {
                actual[rank].Id.Should().Be(expected[rank].Id);
                actual[rank].Score.Should().BeApproximately(expected[rank].Score, 1e-5f);
            }
        }
    }

    [Fact]
    public void KnnSearchBatch_empty_queries_returns_empty_list()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(Spec(4, DistanceMetric.Dot));

        store.KnnSearchBatch(IndexName, Array.Empty<ReadOnlyMemory<float>>(), 5)
            .Should().BeEmpty();
    }

    [Fact]
    public void KnnSearchBatch_rejects_dimension_mismatch_before_scoring()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(Spec(4, DistanceMetric.Dot));
        store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 1, 0, 0, 0 });

        ReadOnlyMemory<float>[] queries =
        [
            new float[] { 1, 0, 0, 0 },
            new float[] { 1, 0 }, // bad — dim 2
        ];

        var act = () => store.KnnSearchBatch(IndexName, queries, k: 1);
        act.Should().Throw<VectorException>()
            .WithMessage("*expects 4 dimensions, got 2 at query[1]*");
    }

    [Fact]
    public void KnnSearchBatch_rejects_non_positive_k()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(Spec(2, DistanceMetric.Dot));

        var act = () => store.KnnSearchBatch(
            IndexName,
            new ReadOnlyMemory<float>[] { new float[] { 1, 0 } },
            k: 0);
        act.Should().Throw<VectorException>().WithMessage("*positive k*");
    }

    [Fact]
    public void KnnSearchBatch_unknown_index_throws()
    {
        var store = new InMemoryVectorStore();
        var act = () => store.KnnSearchBatch(
            "missing",
            new ReadOnlyMemory<float>[] { new float[] { 1 } },
            k: 1);
        act.Should().Throw<VectorException>().WithMessage("*'missing' does not exist*");
    }

    private static float[] RandomVector(Random rng, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return v;
    }

    private static List<(long Id, float Score)> Drain(VectorSearchCursor cursor)
    {
        var list = new List<(long, float)>();
        while (cursor.MoveNext())
        {
            var r = cursor.Current;
            list.Add((r.EntityId, r.Score));
        }
        return list;
    }
}

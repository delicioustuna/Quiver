using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// VEC-1 contract tests for <see cref="InMemoryVectorStore"/>. Covers:
///   (a) hand-calculated KNN agreement for a 100-vector corpus across all metrics,
///   (b) dimension/entity-kind mismatch raises <see cref="VectorException"/>.
/// </summary>
public sealed class InMemoryVectorStoreTests
{
    private const string IndexName = "test-embed";

    private static VectorIndexSpec MakeSpec(int dim, DistanceMetric metric)
        => new(
            IndexName,
            EntityKind.Node,
            new PropertyKeyId(1),
            dim,
            metric,
            ProviderId: "test",
            NormalizationProfile: null);

    [Theory]
    [InlineData(DistanceMetric.Cosine)]
    [InlineData(DistanceMetric.Dot)]
    [InlineData(DistanceMetric.Euclidean)]
    public void KnnSearch_returns_top_k_matching_hand_computed_ranking(DistanceMetric metric)
    {
        const int dim = 8;
        const int n = 100;
        const int k = 7;

        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(dim, metric));

        var rng = new Random(42);
        var vectors = new float[n][];
        for (int i = 0; i < n; i++)
        {
            vectors[i] = NextVector(rng, dim);
            store.SetVector(EntityKind.Node, entityId: i, IndexName, vectors[i]);
        }
        var query = NextVector(rng, dim);

        // Hand-compute expected order: score each vector under the same metric
        // and rank desc.
        var expected = new (long Id, float Score)[n];
        for (int i = 0; i < n; i++)
            expected[i] = (i, ScoreFor(metric, query, vectors[i]));
        Array.Sort(expected, static (a, b) => b.Score.CompareTo(a.Score));

        using var cursor = store.KnnSearch(IndexName, query, k);
        var actual = Drain(cursor);

        actual.Should().HaveCount(k);
        for (int rank = 0; rank < k; rank++)
        {
            actual[rank].EntityKind.Should().Be(EntityKind.Node);
            actual[rank].EntityId.Should().Be(expected[rank].Id);
            actual[rank].Score.Should().BeApproximately(expected[rank].Score, 1e-5f);
        }
    }

    [Fact]
    public void KnnSearch_with_k_greater_than_corpus_returns_all_in_order()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(3, DistanceMetric.Dot));
        store.SetVector(EntityKind.Node, 1, IndexName, new float[] { 1f, 0f, 0f });
        store.SetVector(EntityKind.Node, 2, IndexName, new float[] { 0f, 1f, 0f });
        store.SetVector(EntityKind.Node, 3, IndexName, new float[] { 2f, 0f, 0f });

        using var cursor = store.KnnSearch(IndexName, new float[] { 1f, 0f, 0f }, k: 10);
        var results = Drain(cursor);

        results.Select(r => r.EntityId).Should().ContainInOrder(3L, 1L, 2L);
        results[0].Score.Should().Be(2f);
        results[1].Score.Should().Be(1f);
        results[2].Score.Should().Be(0f);
    }

    [Fact]
    public void RemoveVector_excludes_entity_from_subsequent_KnnSearch()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(2, DistanceMetric.Dot));
        store.SetVector(EntityKind.Node, 1, IndexName, new float[] { 1f, 0f });
        store.SetVector(EntityKind.Node, 2, IndexName, new float[] { 0f, 1f });
        store.RemoveVector(EntityKind.Node, 1, IndexName);

        using var cursor = store.KnnSearch(IndexName, new float[] { 1f, 0f }, k: 5);
        var results = Drain(cursor);

        results.Should().ContainSingle().Which.EntityId.Should().Be(2L);
    }

    [Fact]
    public void SetVector_with_wrong_dimensions_throws()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(4, DistanceMetric.Cosine));

        var act = () => store.SetVector(EntityKind.Node, 1, IndexName, new float[] { 1f, 2f, 3f });
        act.Should().Throw<VectorException>().WithMessage("*expects 4 dimensions, got 3*");
    }

    [Fact]
    public void KnnSearch_with_wrong_query_dimensions_throws()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(4, DistanceMetric.Cosine));

        var act = () => store.KnnSearch(IndexName, new float[] { 1f, 2f }, k: 5);
        act.Should().Throw<VectorException>().WithMessage("*expects 4 dimensions, got 2*");
    }

    [Fact]
    public void SetVector_with_wrong_entity_kind_throws()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(2, DistanceMetric.Cosine));

        var act = () => store.SetVector(
            EntityKind.Relationship, 1, IndexName, new float[] { 1f, 0f });
        act.Should().Throw<VectorException>().WithMessage("*bound to Node but got Relationship*");
    }

    [Fact]
    public void KnnSearch_on_unknown_index_throws()
    {
        var store = new InMemoryVectorStore();
        var act = () => store.KnnSearch("missing", new float[] { 1f }, k: 1);
        act.Should().Throw<VectorException>().WithMessage("*'missing' does not exist*");
    }

    [Fact]
    public void CreateVectorIndex_twice_with_same_name_throws()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(2, DistanceMetric.Cosine));

        var act = () => store.CreateVectorIndex(MakeSpec(2, DistanceMetric.Dot));
        act.Should().Throw<VectorException>().WithMessage("*already exists*");
    }

    [Fact]
    public void DropVectorIndex_removes_index_and_subsequent_use_throws()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(2, DistanceMetric.Cosine));
        store.DropVectorIndex(IndexName);

        var act = () => store.SetVector(EntityKind.Node, 1, IndexName, new float[] { 1f, 0f });
        act.Should().Throw<VectorException>().WithMessage("*does not exist*");
    }

    [Fact]
    public void KnnSearch_with_non_positive_k_throws()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(2, DistanceMetric.Cosine));

        var act = () => store.KnnSearch(IndexName, new float[] { 1f, 0f }, k: 0);
        act.Should().Throw<VectorException>().WithMessage("*positive k*");
    }

    [Fact]
    public void Cosine_metric_treats_parallel_vectors_as_max_similarity()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(3, DistanceMetric.Cosine));
        store.SetVector(EntityKind.Node, 1, IndexName, new float[] { 2f, 0f, 0f });   // parallel
        store.SetVector(EntityKind.Node, 2, IndexName, new float[] { 0f, 1f, 0f });   // orthogonal

        using var cursor = store.KnnSearch(IndexName, new float[] { 5f, 0f, 0f }, k: 2);
        var results = Drain(cursor);

        results[0].EntityId.Should().Be(1L);
        results[0].Score.Should().BeApproximately(1f, 1e-6f);
        results[1].EntityId.Should().Be(2L);
        results[1].Score.Should().BeApproximately(0f, 1e-6f);
    }

    [Fact]
    public void Euclidean_metric_ranks_closer_vectors_first()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(2, DistanceMetric.Euclidean));
        store.SetVector(EntityKind.Node, 1, IndexName, new float[] { 1f, 1f });   // dist sqrt 2
        store.SetVector(EntityKind.Node, 2, IndexName, new float[] { 5f, 5f });   // dist sqrt 50
        store.SetVector(EntityKind.Node, 3, IndexName, new float[] { 0.1f, 0f });   // dist 0.1

        using var cursor = store.KnnSearch(IndexName, new float[] { 0f, 0f }, k: 3);
        var results = Drain(cursor);

        results.Select(r => r.EntityId).Should().ContainInOrder(3L, 1L, 2L);
        // Scores are -distance.
        results[0].Score.Should().BeApproximately(-0.1f, 1e-5f);
        results[1].Score.Should().BeApproximately(-MathF.Sqrt(2f), 1e-5f);
        results[2].Score.Should().BeApproximately(-MathF.Sqrt(50f), 1e-5f);
    }

    // --- helpers ---

    private static float[] NextVector(Random rng, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return v;
    }

    private static float ScoreFor(DistanceMetric metric, float[] q, float[] v)
    {
        return metric switch
        {
            DistanceMetric.Cosine => Cos(q, v),
            DistanceMetric.Dot => Dot(q, v),
            DistanceMetric.Euclidean => -Euc(q, v),
            _ => throw new InvalidOperationException(),
        };

        static float Dot(float[] a, float[] b)
        {
            float s = 0f;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        static float Cos(float[] a, float[] b)
        {
            float dot = 0f, na = 0f, nb = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            float denom = MathF.Sqrt(na) * MathF.Sqrt(nb);
            return denom == 0f ? 0f : dot / denom;
        }

        static float Euc(float[] a, float[] b)
        {
            float s = 0f;
            for (int i = 0; i < a.Length; i++) { float d = a[i] - b[i]; s += d * d; }
            return MathF.Sqrt(s);
        }
    }

    private static List<VectorSearchResult> Drain(VectorSearchCursor cursor)
    {
        var list = new List<VectorSearchResult>();
        while (cursor.MoveNext()) list.Add(cursor.Current);
        return list;
    }
}

using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <see cref="InMemoryVectorStore"/> の契約を検証する。
/// 100 ベクトルのコーパスについて全距離尺度の KNN 結果が手計算と一致すること、
/// 次元数またはエンティティ種別の不一致で <see cref="VectorException"/> が発生することを確認する。
/// </summary>
public sealed class InMemoryVectorStoreTests
{
    private const string IndexName = "test-embed";

    private static VectorIndexSpec MakeSpec(int dim, DistanceMetric metric)
        => new(
            IndexName,
            EntityKind.Vertex,
            new PropertyKeyId(1),
            dim,
            metric,
            ProviderId: "test",
            NormalizationProfile: null);

    [Fact]
    public void CreateVectorIndex_rejects_invalid_hnsw_parameters()
    {
        var valid = MakeSpec(4, DistanceMetric.Cosine);
        VectorIndexSpec[] invalid =
        [
            valid with { HnswM = 1 },
            valid with { HnswMMax0 = 15 },
            valid with { HnswMaxLayers = 0 },
            valid with { HnswEfConstruction = 15 },
        ];

        foreach (var spec in invalid)
        {
            var store = new InMemoryVectorStore();
            var create = () => store.CreateVectorIndex(spec);
            create.Should().Throw<VectorException>();
        }
    }

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
            store.SetVector(EntityKind.Vertex, entityId: i, IndexName, vectors[i]);
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
            actual[rank].EntityKind.Should().Be(EntityKind.Vertex);
            actual[rank].EntityId.Should().Be(expected[rank].Id);
            actual[rank].Score.Should().BeApproximately(expected[rank].Score, 1e-5f);
        }
    }

    [Fact]
    public void KnnSearch_with_k_greater_than_corpus_returns_all_in_order()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(3, DistanceMetric.Dot));
        store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 1f, 0f, 0f });
        store.SetVector(EntityKind.Vertex, 2, IndexName, new float[] { 0f, 1f, 0f });
        store.SetVector(EntityKind.Vertex, 3, IndexName, new float[] { 2f, 0f, 0f });

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
        store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 1f, 0f });
        store.SetVector(EntityKind.Vertex, 2, IndexName, new float[] { 0f, 1f });
        store.RemoveVector(EntityKind.Vertex, 1, IndexName);

        using var cursor = store.KnnSearch(IndexName, new float[] { 1f, 0f }, k: 5);
        var results = Drain(cursor);

        results.Should().ContainSingle().Which.EntityId.Should().Be(2L);
    }

    [Fact]
    public void SetVector_with_wrong_dimensions_throws()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(MakeSpec(4, DistanceMetric.Cosine));

        var act = () => store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 1f, 2f, 3f });
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
            EntityKind.Edge, 1, IndexName, new float[] { 1f, 0f });
        act.Should().Throw<VectorException>().WithMessage("*bound to Vertex but got Edge*");
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

        var act = () => store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 1f, 0f });
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
        store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 2f, 0f, 0f });   // parallel
        store.SetVector(EntityKind.Vertex, 2, IndexName, new float[] { 0f, 1f, 0f });   // orthogonal

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
        store.SetVector(EntityKind.Vertex, 1, IndexName, new float[] { 1f, 1f });   // dist sqrt 2
        store.SetVector(EntityKind.Vertex, 2, IndexName, new float[] { 5f, 5f });   // dist sqrt 50
        store.SetVector(EntityKind.Vertex, 3, IndexName, new float[] { 0.1f, 0f });   // dist 0.1

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

    [Fact]
    public void FlatOnly_SetVector_and_TryGetVector_work()
    {
        var store = new InMemoryVectorStore();
        var spec = new VectorIndexSpec(
            IndexName, EntityKind.Vertex, new PropertyKeyId(1), 4,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly);
        store.CreateVectorIndex(spec);

        float[] vec = [1f, 2f, 3f, 4f];
        store.SetVector(EntityKind.Vertex, 1, IndexName, vec);

        var buf = new float[4];
        store.TryGetVector(EntityKind.Vertex, 1, IndexName, buf).Should().BeTrue();
        buf.Should().Equal(vec);
    }

    [Fact]
    public void FlatOnly_KnnSearch_throws()
    {
        var store = new InMemoryVectorStore();
        var spec = new VectorIndexSpec(
            IndexName, EntityKind.Vertex, new PropertyKeyId(1), 4,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly);
        store.CreateVectorIndex(spec);
        store.SetVector(EntityKind.Vertex, 1, IndexName, [1f, 0f, 0f, 0f]);

        var act = () => store.KnnSearch(IndexName, new float[] { 1f, 0f, 0f, 0f }, 1);
        act.Should().Throw<VectorException>().WithMessage("*FlatOnly*");
    }

    [Fact]
    public void FlatOnly_KnnSearchBatch_throws()
    {
        var store = new InMemoryVectorStore();
        var spec = new VectorIndexSpec(
            IndexName, EntityKind.Vertex, new PropertyKeyId(1), 4,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly);
        store.CreateVectorIndex(spec);
        store.SetVector(EntityKind.Vertex, 1, IndexName, [1f, 0f, 0f, 0f]);

        var queries = new ReadOnlyMemory<float>[] { new float[] { 1f, 0f, 0f, 0f } };
        var act = () => store.KnnSearchBatch(IndexName, queries, 1);
        act.Should().Throw<VectorException>().WithMessage("*FlatOnly*");
    }

    [Fact]
    public void FlatOnly_KnnSearchFiltered_works()
    {
        var store = new InMemoryVectorStore();
        var spec = new VectorIndexSpec(
            IndexName, EntityKind.Vertex, new PropertyKeyId(1), 4,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly);
        store.CreateVectorIndex(spec);

        store.SetVector(EntityKind.Vertex, 1, IndexName, [1f, 0f, 0f, 0f]);
        store.SetVector(EntityKind.Vertex, 2, IndexName, [0f, 1f, 0f, 0f]);
        store.SetVector(EntityKind.Vertex, 3, IndexName, [0f, 0f, 1f, 0f]);

        var candidates = new EntityCandidateSet(EntityKind.Vertex, [1, 2]);
        using var cursor = store.KnnSearchFiltered(IndexName, new float[] { 1f, 0f, 0f, 0f }, 2, candidates);
        var results = Drain(cursor);
        results.Should().HaveCount(2);
        results[0].EntityId.Should().Be(1);
    }

    [Fact]
    public void FlatOnly_RemoveVector_works()
    {
        var store = new InMemoryVectorStore();
        var spec = new VectorIndexSpec(
            IndexName, EntityKind.Vertex, new PropertyKeyId(1), 4,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly);
        store.CreateVectorIndex(spec);

        store.SetVector(EntityKind.Vertex, 1, IndexName, [1f, 0f, 0f, 0f]);
        var buf = new float[4];
        store.TryGetVector(EntityKind.Vertex, 1, IndexName, buf).Should().BeTrue();

        store.RemoveVector(EntityKind.Vertex, 1, IndexName);
        store.TryGetVector(EntityKind.Vertex, 1, IndexName, buf).Should().BeFalse();
    }

    [Fact]
    public void TryGetIndex_returns_IndexKind()
    {
        var store = new InMemoryVectorStore();
        var spec = new VectorIndexSpec(
            IndexName, EntityKind.Vertex, new PropertyKeyId(1), 4,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly);
        store.CreateVectorIndex(spec);

        store.TryGetIndex(IndexName, out var got).Should().BeTrue();
        got.IndexKind.Should().Be(VectorIndexKind.FlatOnly);
    }

    private static List<VectorSearchResult> Drain(VectorSearchCursor cursor)
    {
        var list = new List<VectorSearchResult>();
        while (cursor.MoveNext()) list.Add(cursor.Current);
        return list;
    }
}

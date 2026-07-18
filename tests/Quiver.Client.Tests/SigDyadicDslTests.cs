using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>
/// SIG トラックの ApplyDyadic DSL チェーン構築テスト。
/// 統合テスト (ApplyDyadicConstraintTests) と重複しない DSL 構築の単体テストに注力する。
/// </summary>
public sealed class SigDyadicDslTests : IDisposable
{
    private const string VecIndex = "Embedding";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public SigDyadicDslTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sig_dsl_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        PropertyKeyId keyId;
        using (var schemaTx = _db.BeginWriteTransaction())
        {
            keyId = schemaTx.EditSchema.GetOrCreatePropertyKey(VecIndex);
            schemaTx.Commit();
        }
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VecIndex, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly));

        using var tx = _db.BeginWriteTransaction();
        for (int i = 0; i < 5; i++)
        {
            var nid = tx.CreateVertex("Signal");
            tx.SetProperty(nid, "Site", PropertyValue.FromString($"S{i}"));
            var vec = new float[Dim];
            vec[i % Dim] = 1f;
            tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex, vec);
            tx.SetProperty(nid, "Embedding", PropertyValue.FromFloatArray(vec));
        }
        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── literal vector b を使う ApplyDyadic ──────────────────────────

    [Fact]
    public void ApplyDyadic_DotProduct_chains_and_returns_results()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var hits = g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    [Fact]
    public void ApplyDyadic_CosineSimilarity_chains_and_returns_results()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var hits = g.Vertices<SignalVertex>()
            .ApplyDyadic<CosineSimilarityOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    [Fact]
    public void ApplyDyadic_EuclideanDistance_chains_and_returns_results()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var hits = g.Vertices<SignalVertex>()
            .ApplyDyadic<EuclideanDistanceOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    // ── Has filter chain を使う ApplyDyadic ──────────────────────────

    [Fact]
    public void ApplyDyadic_chains_after_Has_filter()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var hits = g.Vertices<SignalVertex>()
            .Has(s => s.Site, "S0")
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 1)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(1);
    }

    // ── region を使う ApplyDyadic ────────────────────────────────────

    [Fact]
    public void ApplyDyadic_with_regions_restricts_scoring()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var hits = g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(
                s => s.Embedding,
                [1f, 0f, 0f, 0f],
                regions: [0..2],
                k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    // ── ApplyDyadic の引数検証 ───────────────────────────────────────

    [Fact]
    public void ApplyDyadic_null_b_throws()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var act = () => g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, (float[])null!, k: 3);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ApplyDyadic_zero_k_throws()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var act = () => g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ApplyDyadic_negative_k_throws()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var act = () => g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ApplyDyadic_negative_oversample_throws()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var act = () => g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3, oversample: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── traversal ベースの b を使う ApplyDyadic (非相関 sub-query) ──

    [Fact]
    public void ApplyDyadic_with_traversal_b_chains_correctly()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // 別Vertexの embedding を query vector として使う
        var refTraversal = g.Vertices<SignalVertex>()
            .Has(s => s.Site, "S0")
            .Values(s => s.Embedding);

        var hits = g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, refTraversal, k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    [Fact]
    public void ApplyDyadic_traversal_b_null_throws()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var act = () => g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, (GraphTraversal<float[]>)null!, k: 3);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── GraphTraversal<float[]> DSL (Values for float[] property) ─────

    [Fact]
    public void Values_float_array_returns_vectors()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var vectors = g.Vertices<SignalVertex>()
            .Has(s => s.Site, "S0")
            .Values(s => s.Embedding)
            .ToList();

        vectors.Should().ContainSingle();
        vectors[0].Should().HaveCount(Dim);
    }

    // ── ApplyDyadic 結果の追加 chain ─────────────────────────────────

    [Fact]
    public void ApplyDyadic_result_supports_Count()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var count = g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .Count();

        count.Should().BeGreaterThan(0);
        count.Should().BeLessOrEqualTo(3);
    }

    [Fact]
    public void ApplyDyadic_result_supports_First()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var first = g.Vertices<SignalVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .First();

        first.Should().NotBeNull();
    }

    // ── 最小 IGraphVertex 型 ───────────────────────────────────────────

    private sealed class SignalVertex : IGraphVertex<SignalVertex>
    {
        public string Site { get; set; } = "";
        public float[] Embedding { get; set; } = [];

        public static string GraphLabel => "Signal";

        public static VertexId Insert(IWriteTransaction tx, SignalVertex entity)
        {
            var id = tx.CreateVertex(GraphLabel);
            tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
            return id;
        }

        public static VertexId InsertIndexed(IWriteTransaction tx, SignalVertex entity) => Insert(tx, entity);

        public static SignalVertex Load(IReadTransaction tx, VertexId id)
        {
            var vertex = new SignalVertex
            {
                Site = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Site").Utf8StringValue),
            };
            var emb = tx.GetProperty(id, "Embedding");
            if (emb.Type == PropertyValueType.FloatArray)
                vertex.Embedding = emb.FloatArrayValue.ToArray();
            return vertex;
        }

        public static void Update(IWriteTransaction tx, VertexId id, SignalVertex entity)
            => tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));

        public static void Delete(IWriteTransaction tx, VertexId id) => tx.DeleteVertex(id);
    }
}

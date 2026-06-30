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
    private readonly GraphDatabase _db;

    public SigDyadicDslTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sig_dsl_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.Schema.GetOrCreatePropertyKey(VecIndex);
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VecIndex, EntityKind.Node, keyId, Dim,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly));

        using var tx = _db.BeginTransaction();
        for (int i = 0; i < 5; i++)
        {
            var nid = tx.CreateNode("Signal");
            tx.SetProperty(nid, "Site", PropertyValue.FromString($"S{i}"));
            var vec = new float[Dim];
            vec[i % Dim] = 1f;
            _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, vec);
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
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var hits = g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    [Fact]
    public void ApplyDyadic_CosineSimilarity_chains_and_returns_results()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var hits = g.Nodes<SignalNode>()
            .ApplyDyadic<CosineSimilarityOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    [Fact]
    public void ApplyDyadic_EuclideanDistance_chains_and_returns_results()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var hits = g.Nodes<SignalNode>()
            .ApplyDyadic<EuclideanDistanceOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    // ── Has filter chain を使う ApplyDyadic ──────────────────────────

    [Fact]
    public void ApplyDyadic_chains_after_Has_filter()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var hits = g.Nodes<SignalNode>()
            .Has(s => s.Site, "S0")
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 1)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(1);
    }

    // ── region を使う ApplyDyadic ────────────────────────────────────

    [Fact]
    public void ApplyDyadic_with_regions_restricts_scoring()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var hits = g.Nodes<SignalNode>()
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
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () => g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, (float[])null!, k: 3);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ApplyDyadic_zero_k_throws()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () => g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ApplyDyadic_negative_k_throws()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () => g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ApplyDyadic_negative_oversample_throws()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () => g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3, oversample: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── traversal ベースの b を使う ApplyDyadic (非相関 sub-query) ──

    [Fact]
    public void ApplyDyadic_with_traversal_b_chains_correctly()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // 別ノードの embedding を query vector として使う
        var refTraversal = g.Nodes<SignalNode>()
            .Has(s => s.Site, "S0")
            .Values(s => s.Embedding);

        var hits = g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, refTraversal, k: 3)
            .ToList();

        hits.Should().HaveCountLessOrEqualTo(3);
    }

    [Fact]
    public void ApplyDyadic_traversal_b_null_throws()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () => g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, (GraphTraversal<float[]>)null!, k: 3);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── GraphTraversal<float[]> DSL (Values for float[] property) ─────

    [Fact]
    public void Values_float_array_returns_vectors()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var vectors = g.Nodes<SignalNode>()
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
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var count = g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .Count();

        count.Should().BeGreaterThan(0);
        count.Should().BeLessOrEqualTo(3);
    }

    [Fact]
    public void ApplyDyadic_result_supports_First()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var first = g.Nodes<SignalNode>()
            .ApplyDyadic<DotProductOp>(s => s.Embedding, [1f, 0f, 0f, 0f], k: 3)
            .First();

        first.Should().NotBeNull();
    }

    // ── 最小 IGraphNode 型 ───────────────────────────────────────────

    private sealed class SignalNode : IGraphNode<SignalNode>
    {
        public string Site { get; set; } = "";
        public float[] Embedding { get; set; } = [];

        public static string GraphLabel => "Signal";

        public static NodeId Insert(IGraphTransaction tx, SignalNode entity)
        {
            var id = tx.CreateNode(GraphLabel);
            tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
            return id;
        }

        public static NodeId InsertIndexed(IGraphTransaction tx, SignalNode entity) => Insert(tx, entity);

        public static SignalNode Load(IGraphTransaction tx, NodeId id)
        {
            var node = new SignalNode
            {
                Site = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Site").Utf8StringValue),
            };
            var emb = tx.GetProperty(id, "Embedding");
            if (emb.Type == PropertyValueType.FloatArray)
                node.Embedding = emb.FloatArrayValue.ToArray();
            return node;
        }

        public static void Update(IGraphTransaction tx, NodeId id, SignalNode entity)
            => tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));

        public static void Delete(IGraphTransaction tx, NodeId id) => tx.DeleteNode(id);
    }
}

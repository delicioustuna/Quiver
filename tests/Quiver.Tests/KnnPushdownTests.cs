using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// VEC-9 coverage: post-filter push-down rewrite that converts
/// <c>g.Knn(...).HasLabel(...).Has(...).ToList()</c> into a graph-first
/// <see cref="KnnOp"/> (Candidate != null) plan via <c>LogicalOptimizer</c>.
/// Verifies result equivalence with the manual
/// <c>g.Nodes().HasLabel(...).FilterByKnn(...)</c> form, the "k starvation"
/// bug fix, the Limit-driven K shrink optimization, and the fall-back to
/// vector-first when no selectivity hint exists.
/// </summary>
public sealed class KnnPushdownTests : IDisposable
{
    private const string IndexName = "doc-embed";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public KnnPushdownTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_vec9_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.Schema.GetOrCreatePropertyKey("title");
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, keyId, Dim,
            DistanceMetric.Cosine, "test", null));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void SeedDocsAndArticles(out long[] docIds, out long[] articleIds)
    {
        docIds = new long[5];
        articleIds = new long[5];
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < 5; i++)
        {
            var d = tx.CreateNode("Doc");
            docIds[i] = d.Value;
            var v = new float[Dim];
            v[i % Dim] = 1f;
            _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, v);
        }
        for (int i = 0; i < 5; i++)
        {
            var a = tx.CreateNode("Article");
            articleIds[i] = a.Value;
            var v = new float[Dim];
            v[0] = 1f;
            _db.Vectors.SetVector(EntityKind.Node, a.Value, IndexName, v);
        }
        tx.Commit();
    }

    [Fact]
    public void Knn_HasLabel_pushdown_returns_same_set_as_manual_FilterByKnn()
    {
        SeedDocsAndArticles(out _, out _);

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var query = new float[] { 1f, 0f, 0f, 0f };

        var pushdown = g.Knn(IndexName, query, k: 2).HasLabel("Doc").ToList();
        var manual = g.Nodes().HasLabel("Doc").FilterByKnn(IndexName, query, k: 2).ToList();

        pushdown.Select(n => n.Value).Should().BeEquivalentTo(manual.Select(n => n.Value));
    }

    [Fact]
    public void Knn_HasLabel_pushdown_returns_full_k_when_postfilter_starves()
    {
        // 2 Doc + 50 Article, all sharing the same query direction. Post-filter
        // semantics would pull top-2 from all 52 (= 2 Articles), then HasLabel
        // drops both → 0 results. Push-down filters labels first → 2 Docs.
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                var a = tx.CreateNode("Article");
                _db.Vectors.SetVector(EntityKind.Node, a.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            for (int i = 0; i < 2; i++)
            {
                var d = tx.CreateNode("Doc");
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, new float[] { 0.5f, 0.5f, 0, 0 });
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var result = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 2)
            .HasLabel("Doc")
            .ToList();

        result.Should().HaveCount(2);
    }

    [Fact]
    public void Knn_Has_pushdown_filters_on_property()
    {
        using (var tx = _db.BeginTransaction())
        {
            // 3 Doc, only 1 with status=active. Push-down must keep only that.
            for (int i = 0; i < 3; i++)
            {
                var d = tx.CreateNode("Doc");
                tx.SetProperty(d, "status", PropertyValue.FromString(i == 1 ? "active" : "archived"));
                var v = new float[Dim];
                v[0] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, v);
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var result = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 5)
            .HasLabel("Doc")
            .Has("status", "active")
            .ToList();

        result.Should().HaveCount(1);
    }

    [Fact]
    public void Knn_Limit_smaller_than_k_shrinks_KNN_k()
    {
        // 無 filter chain → vector-first. Limit が KNN の K を 5 に縮める。
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var optimized = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 20).Limit(5).Optimized();

        optimized.Should().BeOfType<KnnOp>();
        var knn = (KnnOp)optimized;
        knn.K.Should().Be(5);
        knn.Candidate.Should().BeNull("filter chain が無いため vector-first");
    }

    [Fact]
    public void Knn_HasLabel_Limit_smaller_than_k_shrinks_FilteredKnn_k()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var optimized = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 20)
            .HasLabel("Doc")
            .Limit(5)
            .Optimized();

        optimized.Should().BeOfType<KnnOp>();
        var knn = (KnnOp)optimized;
        knn.K.Should().Be(5);
        knn.Candidate.Should().NotBeNull("HasLabel → graph-first");
    }

    [Fact]
    public void Knn_Limit_greater_than_k_keeps_K_unchanged()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var optimized = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 5).Limit(20).Optimized();

        optimized.Should().BeOfType<KnnOp>();
        ((KnnOp)optimized).K.Should().Be(5);
    }

    [Fact]
    public void Knn_no_filter_chain_materializes_to_vector_first()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var optimized = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 5).Optimized();

        optimized.Should().BeOfType<KnnOp>();
        ((KnnOp)optimized).Candidate.Should().BeNull("vector-first");
    }

    [Fact]
    public void Knn_HasLabel_materializes_to_graph_first()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var optimized = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 5).HasLabel("Doc").Optimized();

        optimized.Should().BeOfType<KnnOp>();
        ((KnnOp)optimized).Candidate.Should().NotBeNull("graph-first");
    }

    [Fact]
    public void Knn_HasLabel_with_zero_candidates_returns_empty()
    {
        // No "Missing" label exists in the graph → candidate set is empty →
        // FilteredKnn short-circuits without throwing.
        SeedDocsAndArticles(out _, out _);

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var result = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 3)
            .HasLabel("Missing")
            .ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void Knn_Out_after_pushdown_traverses_neighbors()
    {
        // 3 Doc, the best-scoring Doc has one outgoing REFERENCES edge to
        // a 4th node. After push-down the result of .Out("REFERENCES") must
        // contain that 4th node.
        long target;
        using (var tx = _db.BeginTransaction())
        {
            var bestDoc = tx.CreateNode("Doc");
            _db.Vectors.SetVector(EntityKind.Node, bestDoc.Value, IndexName, new float[] { 1, 0, 0, 0 });
            for (int i = 1; i < 3; i++)
            {
                var d = tx.CreateNode("Doc");
                var v = new float[Dim];
                v[i] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, v);
            }
            var t = tx.CreateNode("Other");
            target = t.Value;
            tx.CreateRelationship(bestDoc, t, "REFERENCES");
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var neighbors = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 1)
            .HasLabel("Doc")
            .Out("REFERENCES")
            .ToList();

        neighbors.Select(n => n.Value).Should().Contain(target);
    }

    [Fact]
    public void Knn_As_Select_pushdown_preserves_alias()
    {
        SeedDocsAndArticles(out var docIds, out _);

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // .As("a") right after Knn must survive the push-down materialize
        // (FilteredKnn output is 1 column NodeId, so alias col = 0 stays valid).
        var result = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 2)
            .As("a")
            .HasLabel("Doc")
            .Select("a")
            .ToList();

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(n => docIds.Contains(n.Value));
    }
}

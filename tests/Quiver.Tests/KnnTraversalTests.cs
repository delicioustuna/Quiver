using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// トラバーサル起点としての <c>g.Knn(...)</c> をエンドツーエンドに検証する。
/// ベクトル検索を既存のラベル、プロパティ、展開パイプラインと合成できることを確認する。
/// 参照ストアには全走査の <c>InMemoryVectorStore</c> を使い、
/// スコア順位や次元数および k の検査ではなく、トラバーサル合成だけを対象とする。
/// </summary>
public sealed class KnnTraversalTests : IDisposable
{
    private const string IndexName = "doc-embed";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public KnnTraversalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_vec5_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Knn_source_returns_top_k_in_similarity_order()
    {
        // Three Doc nodes, each with a distinct one-hot vector so the
        // expected ranking is unambiguous.
        var ids = new long[3];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                var nid = tx.CreateNode("Doc");
                ids[i] = nid.Value;
                var vec = new float[Dim];
                vec[i] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, nid.Value, IndexName, vec);
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Query parallel to ids[1] → expect ids[1] first.
        var query = new float[] { 0f, 1f, 0f, 0f };
        var result = g.Knn(IndexName, query, k: 2).ToList();

        result.Should().HaveCount(2);
        result[0].Value.Should().Be(ids[1]);
    }

    [Fact]
    public void Knn_composes_with_HasLabel_and_Out()
    {
        // Mix Doc + Article nodes in the same vector index; HasLabel("Doc")
        // must trim the candidate set after KNN orders it. Each Doc points
        // at one Author via REFERENCES so the .Out() leg yields Author ids.
        var docIds = new long[3];
        var authorIds = new long[3];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                var author = tx.CreateNode("Author");
                authorIds[i] = author.Value;
            }
            for (int i = 0; i < 3; i++)
            {
                var doc = tx.CreateNode("Doc");
                docIds[i] = doc.Value;
                var v = new float[Dim];
                v[i] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, doc.Value, IndexName, v);
                tx.CreateRelationship(doc, new NodeId(authorIds[i]), "REFERENCES");
            }
            // One Article that would beat all Docs on cosine to the query
            // vector below — must be filtered out by HasLabel("Doc").
            var article = tx.CreateNode("Article");
            var articleVec = new float[] { 0f, 1f, 0f, 0f };
            _db.Vectors.SetVector(EntityKind.Node, article.Value, IndexName, articleVec);

            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var query = new float[] { 0f, 1f, 0f, 0f };
        var authorsViaKnn = g.Knn(IndexName, query, k: 5)
            .HasLabel("Doc")
            .Out("REFERENCES")
            .ToList();

        // The Article (best score) is filtered out; among the Docs the
        // one whose vector aligns with the query is closest → its author
        // is first.
        authorsViaKnn.Should().NotBeEmpty();
        authorsViaKnn[0].Value.Should().Be(authorIds[1]);
        authorsViaKnn.Should().OnlyContain(a => Array.IndexOf(authorIds, a.Value) >= 0);
    }

    [Fact]
    public void Knn_throws_on_dimension_mismatch()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var wrongDim = new float[Dim + 1];
        var act = () =>
        {
            // The throw happens on Open (first MoveNext), so we have to force enumeration.
            _ = g.Knn(IndexName, wrongDim, 3).ToList();
        };
        act.Should().Throw<VectorException>();
    }

    [Fact]
    public void Knn_throws_on_unknown_index()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () =>
        {
            _ = g.Knn("does-not-exist", new float[Dim], 3).ToList();
        };
        act.Should().Throw<VectorException>();
    }
}

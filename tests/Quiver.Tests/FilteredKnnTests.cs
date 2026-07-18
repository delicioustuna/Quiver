using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// graph-first のフィルター付き KNN と、オプティマイザーによる
/// vector-first / graph-first 戦略の選択を検証する。
/// <c>FilterByKnn</c> がラベル集合と上位 k 件の正しい積集合を返すこと、
/// 小さいフロンティアでは graph-first、大きいフロンティアでは vector-first を選ぶこと、
/// 対象がインデックス順位の深い位置にあっても候補追加取得で発見できることを確認する。
/// </summary>
public sealed class FilteredKnnTests : IDisposable
{
    private const string IndexName = "doc-embed";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public FilteredKnnTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_vec6_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.EditSchema(schema => schema.GetOrCreatePropertyKey("title"));
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "test", null));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void FilterByKnn_returns_top_k_within_label_frontier()
    {
        // Five Docs vs five Articles, all in the same vector index. The query
        // is closest to a single Article (which must be excluded by HasLabel),
        // then to a specific Doc.
        var docIds = new long[5];
        var articleIds = new long[5];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var d = tx.CreateVertex("Doc");
                docIds[i] = d.Value;
                var v = new float[Dim];
                v[i % Dim] = 1f;
                tx.SetVector(EntityKind.Vertex, d.Value, IndexName, v);
            }
            for (int i = 0; i < 5; i++)
            {
                var a = tx.CreateVertex("Article");
                articleIds[i] = a.Value;
                var v = new float[Dim];
                // Articles get an exact match on the query, Docs get less.
                v[0] = 1f;
                tx.SetVector(EntityKind.Vertex, a.Value, IndexName, v);
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var query = new float[] { 1f, 0f, 0f, 0f };
        var topDocs = g.Vertices().HasLabel("Doc")
            .FilterByKnn(IndexName, query, k: 2)
            .ToList();

        topDocs.Should().HaveCount(2);
        // No Article id should leak through.
        topDocs.Should().OnlyContain(n => Array.IndexOf(articleIds, n.Value) < 0);
        // The Doc that happens to share v[0]=1 (i=0 → docIds[0]) is the best.
        topDocs[0].Value.Should().Be(docIds[0]);
    }

    [Fact]
    public void FilterByKnn_returns_empty_when_no_candidates()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            var n = tx.CreateVertex("Doc");
            tx.SetVector(EntityKind.Vertex, n.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // No vertices carry the label "Missing" → candidate set is empty →
        // KnnSearchFiltered must short-circuit, not throw or return Docs.
        var result = g.Vertices().HasLabel("Missing")
            .FilterByKnn(IndexName, new float[] { 1, 0, 0, 0 }, k: 3)
            .ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterByKnn_recovers_matches_via_oversample()
    {
        // 50 vertices; only 2 of them carry label "Doc" and they happen to be
        // the lowest-scoring vectors against the query. The oversample loop
        // must enlarge candidateK until it surfaces them.
        var docIds = new List<long>();
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                bool isDoc = (i == 48 || i == 49);
                var n = tx.CreateVertex(isDoc ? "Doc" : "Other");
                if (isDoc) docIds.Add(n.Value);
                var v = new float[Dim];
                // Score-against-query (1,0,0,0): higher index → lower score.
                v[0] = (50 - i) / 50f;
                v[1] = i / 50f;
                tx.SetVector(EntityKind.Vertex, n.Value, IndexName, v);
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var result = g.Vertices().HasLabel("Doc")
            .FilterByKnn(IndexName, new float[] { 1, 0, 0, 0 }, k: 2)
            .ToList();

        result.Select(n => n.Value).Should().BeEquivalentTo(docIds);
    }

    // KnnStrategy は internal のため、[Theory] の公開シグネチャでは int に投影する。
    [Theory]
    [InlineData(8L,       100,  10,    (int)KnnStrategy.GraphFirst)]   // tiny candidate set
    [InlineData(100L,    1_000, 50,    (int)KnnStrategy.GraphFirst)]   // 10% of index
    [InlineData(50L,    10_000, 10,    (int)KnnStrategy.GraphFirst)]   // 0.5% → graph-first
    [InlineData(5_000L, 10_000, 10,    (int)KnnStrategy.VectorFirst)]  // 50% → vector-first
    [InlineData(0L,     10_000, 10,    (int)KnnStrategy.VectorFirst)]  // unknown → vector-first
    public void ChooseKnnStrategy_picks_by_candidate_fraction(
        long candidateCount, long totalIndexed, int k, int expected)
    {
        var optimizer = _db.CreateOptimizer();
        var actual = optimizer.ChooseKnnStrategy(candidateCount, k, totalIndexed);
        ((int)actual).Should().Be(expected);
    }
}

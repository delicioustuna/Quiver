using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <c>g.HybridSearch(...)</c> が BM25 と KNN の上位 k 件を
/// RRF (k0=60) で融合する処理をエンドツーエンドに検証する。
/// 合成データで各検索経路の順位を固定し、その順位から融合結果を手計算することで、
/// 生のスコアではなく RRF の振る舞いを確認する。
/// </summary>
public sealed class HybridSearchTests : IDisposable
{
    private const string TextIndex = "idx_body";
    private const string VectorIndex = "doc-embed";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public HybridSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts5_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));
        _db.Schema.CreateFullTextIndex(TextIndex, "Doc", "body");
        var keyId = _db.Schema.GetOrCreatePropertyKey("embedding");
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VectorIndex, EntityKind.Vertex, keyId, Dim, DistanceMetric.Cosine, "test", null));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Creates a Doc with an optional body (full-text) and optional vector (KNN).</summary>
    private VertexId AddDoc(string? body, float[]? vector)
    {
        using var tx = _db.BeginTransaction();
        var n = tx.CreateVertex("Doc");
        if (body is not null) tx.SetProperty(n, "body", PropertyValue.FromString(body));
        if (vector is not null) _db.Vectors.SetVector(EntityKind.Vertex, n.Value, VectorIndex, vector);
        tx.Commit();
        return n;
    }

    private List<VertexId> Hybrid(string queryText, float[] queryVector, int k)
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        return rtx.G(_db.Schema)
            .HybridSearch(TextIndex, queryText, VectorIndex, queryVector, k)
            .ToList();
    }

    [Fact]
    public void Consensus_doc_outranks_docs_that_top_only_one_channel()
    {
        // Pins the RRF *shape* — a doc ranked decently in BOTH channels beats a doc
        // ranked #1 in only ONE channel. (This ordering holds for any k0 > 1, so it does
        // not pin the k0=60 constant itself; the numbers below use k0=60 to illustrate.)
        // Query: text "alpha", vector [1,0,0,0].
        // BM25 ranking (query "alpha"): X(tf=2) > A(tf=1,short) > B(tf=1,long); Y has no "alpha".
        // KNN  ranking (cos to [1,0,0,0]): Y(1.0) > A(0.8) > B(0.6) > X(0.0).
        // With k=3 each channel emits 3: BM25=[X,A,B], KNN=[Y,A,B].
        // RRF(k0=60): A=1/62+1/62=.03226, B=1/63+1/63=.03175, X=1/61=.01639, Y=1/61=.01639.
        // 両方で 2 位と 3 位の A、B は、片方だけで 1 位の X、Y より高くなる。
        var x = AddDoc("alpha alpha", new float[] { 0f, 1f, 0f, 0f });        // text-strong, vector-orthogonal
        var a = AddDoc("alpha", new float[] { 0.8f, 0.6f, 0f, 0f });          // both, mid
        var b = AddDoc("alpha beta gamma", new float[] { 0.6f, 0.8f, 0f, 0f }); // both, lower
        var y = AddDoc("delta", new float[] { 1f, 0f, 0f, 0f });             // vector-best, no text

        var result = Hybrid("alpha", new float[] { 1f, 0f, 0f, 0f }, k: 3);

        result.Should().HaveCount(3);
        result[0].Should().Be(a, "A is ranked high in both channels (RRF consensus winner)");
        result[1].Should().Be(b, "B is the next consensus doc");
        // X and Y each top exactly one channel and tie on RRF; only the higher
        // ID 昇順の同点解消で、先に作成した X が最終上位 3 件に入る。
        result[2].Should().Be(x);
        result.Should().NotContain(y);
    }

    [Fact]
    public void Doc_found_by_only_one_channel_still_surfaces()
    {
        // P is in the full-text index only (no vector); Q is in the vector index
        // only (no body). Each is found by exactly one channel — both must appear
        // in the fused output.
        var p = AddDoc("alpha", vector: null);
        var q = AddDoc(body: null, new float[] { 1f, 0f, 0f, 0f });

        var result = Hybrid("alpha", new float[] { 1f, 0f, 0f, 0f }, k: 5);

        result.Should().HaveCount(2);
        result.Should().Contain(p).And.Contain(q);
    }

    [Fact]
    public void Proper_noun_query_leans_to_bm25_paraphrase_leans_to_knn()
    {
        // "quiver" is a rare exact term only doc1 contains; doc2 is a semantic
        // paraphrase (different words, near vector). The proper-noun query ranks
        // the BM25 hit first; the paraphrase still surfaces via the vector channel.
        var exact = AddDoc("quiver embedded graph database", new float[] { 0.2f, 1f, 0f, 0f });
        var para = AddDoc("in-process vector store engine", new float[] { 1f, 0f, 0f, 0f });

        // Query vector aligned with the paraphrase doc's vector.
        var result = Hybrid("quiver", new float[] { 1f, 0f, 0f, 0f }, k: 5);

        result.Should().Contain(exact, "the exact 'quiver' term is matched by BM25");
        result.Should().Contain(para, "the paraphrase is matched by the vector channel");
        result[0].Should().Be(exact, "BM25 rank-1 + decent vector rank wins the fusion");
    }

    [Fact]
    public void Hybrid_search_composes_with_Out_traversal()
    {
        VertexId author;
        using (var tx = _db.BeginTransaction())
        {
            author = tx.CreateVertex("Author");
            var doc = tx.CreateVertex("Doc");
            tx.SetProperty(doc, "body", PropertyValue.FromString("quiver report"));
            _db.Vectors.SetVector(EntityKind.Vertex, doc.Value, VectorIndex, new float[] { 1f, 0f, 0f, 0f });
            tx.CreateEdge(doc, author, "WROTE");
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var authors = rtx.G(_db.Schema)
            .HybridSearch(TextIndex, "quiver", VectorIndex, new float[] { 1f, 0f, 0f, 0f }, k: 10)
            .Out("WROTE")
            .ToList();

        authors.Should().ContainSingle().Which.Should().Be(author);
    }

    [Fact]
    public void Empty_query_text_still_returns_vector_hits()
    {
        // Tokenizing an empty/symbol-only query yields zero terms → BM25 emits
        // nothing; the fused result is then exactly the KNN ranking.
        var d1 = AddDoc("anything", new float[] { 1f, 0f, 0f, 0f });
        var d2 = AddDoc("whatever", new float[] { 0f, 1f, 0f, 0f });

        var result = Hybrid("", new float[] { 1f, 0f, 0f, 0f }, k: 5);

        result.Should().HaveCount(2);
        result[0].Should().Be(d1, "closest vector ranks first when the text channel is empty");
    }
}

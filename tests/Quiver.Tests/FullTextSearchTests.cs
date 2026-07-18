using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// BM25 上位 k 件を返すトラバーサル起点 <c>g.Search(...)</c> を
/// エンドツーエンドに検証する。
/// 期待順位は既定値 k1=1.2、b=0.75 を使って手計算する。
/// </summary>
public sealed class FullTextSearchTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public FullTextSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts3_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));
        _db.EditSchema(schema => schema.CreateFullTextIndex(Index, "Doc", "body"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private VertexId AddDoc(string body)
    {
        using var tx = _db.BeginWriteTransaction();
        var n = tx.CreateVertex("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString(body));
        tx.Commit();
        return n;
    }

    private List<VertexId> Search(string query, int k)
    {
        using var rtx = _db.BeginReadTransaction();
        return rtx.Query.Search(Index, query, k).ToList();
    }

    [Fact]
    public void Higher_term_frequency_ranks_first_at_equal_length()
    {
        // Equal doc length (2 tokens). N=2, df(x)=2, avgdl=2, idf(x)=ln(1.2)≈0.182.
        // doc1 "x y": tf=1 → score idf*1.0.  doc2 "x x": tf=2 → score idf*1.375.
        var doc1 = AddDoc("x y");
        var doc2 = AddDoc("x x");

        var result = Search("x", k: 10);

        result.Should().HaveCount(2);
        result[0].Should().Be(doc2);
        result[1].Should().Be(doc1);
    }

    [Fact]
    public void Matching_a_rarer_term_boosts_ranking()
    {
        // "apple" in all 3 (df=3, low idf); "banana" only in doc1 (df=1, high idf).
        // Query "apple banana": doc1 gets apple+banana, doc2/doc3 only apple.
        var doc1 = AddDoc("apple banana");
        var doc2 = AddDoc("apple cherry");
        var doc3 = AddDoc("apple date");

        var result = Search("apple banana", k: 10);

        result.Should().HaveCount(3);
        result[0].Should().Be(doc1, "doc1 matches the rare term 'banana'");
        result.Should().Contain(new[] { doc2, doc3 });
    }

    [Fact]
    public void Read_your_own_writes_search_hit_within_same_transaction()
    {
        // DoD: a document written earlier in a transaction is found by g.Search
        // within that same (uncommitted) transaction.
        using var tx = _db.BeginWriteTransaction();
        var n = tx.CreateVertex("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString("inflight searchable text"));

        var hits = tx.Query.Search(Index, "inflight", k: 10).ToList();
        hits.Should().ContainSingle().Which.Should().Be(n);

        tx.Commit();
    }

    [Fact]
    public void Search_limits_to_k()
    {
        AddDoc("term a"); AddDoc("term b"); AddDoc("term c");
        Search("term", k: 2).Should().HaveCount(2);
    }

    [Fact]
    public void Japanese_bigram_query_matches_indexed_bigrams()
    {
        var tokyo = AddDoc("東京都");   // bigrams 東京, 京都 + unigrams 東, 京, 都
        AddDoc("京都府");               // bigrams 京都, 都府 + unigrams 京, 都, 府

        // Query "東京" → bigrams 東京 + unigrams 東, 京.
        // Both docs match via unigram "京", but tokyo ranks first (3 term matches vs 1).
        var result = Search("東京", k: 10);
        result.Should().HaveCount(2);
        result[0].Should().Be(tokyo);
        // Query "京都" → bigram 京都 → both docs contain it.
        Search("京都", k: 10).Should().HaveCount(2);
    }

    [Fact]
    public void Deleted_vertex_is_not_returned()
    {
        var doc = AddDoc("secret content");
        Search("secret", k: 10).Should().ContainSingle().Which.Should().Be(doc);

        using (var tx = _db.BeginWriteTransaction())
        {
            tx.DeleteVertex(doc);
            tx.Commit();
        }

        Search("secret", k: 10).Should().BeEmpty();
    }

    [Fact]
    public void Search_composes_with_Out_traversal()
    {
        VertexId author;
        VertexId doc;
        using (var tx = _db.BeginWriteTransaction())
        {
            author = tx.CreateVertex("Author");
            doc = tx.CreateVertex("Doc");
            tx.SetProperty(doc, "body", PropertyValue.FromString("quiver report"));
            tx.CreateEdge(doc, author, "WROTE");
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var authors = rtx.Query.Search(Index, "quiver", k: 10).Out("WROTE").ToList();

        authors.Should().ContainSingle().Which.Should().Be(author);
    }
}

using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FTS-3 end-to-end: <c>g.Search(...)</c> as a BM25 top-k traversal source.
/// Rankings below are hand-computed with k1=1.2, b=0.75 (design 13 §6 defaults).
/// </summary>
public sealed class FullTextSearchTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public FullTextSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts3_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));
        _db.Schema.CreateFullTextIndex(Index, "Doc", "body");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private NodeId AddDoc(string body)
    {
        using var tx = _db.BeginTransaction();
        var n = tx.CreateNode("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString(body));
        tx.Commit();
        return n;
    }

    private List<NodeId> Search(string query, int k)
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        return rtx.G(_db.Schema).Search(Index, query, k).ToList();
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
        using var tx = _db.BeginTransaction();
        var n = tx.CreateNode("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString("inflight searchable text"));

        var hits = tx.G(_db.Schema).Search(Index, "inflight", k: 10).ToList();
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
        var tokyo = AddDoc("東京都");   // bigrams 東京, 京都
        AddDoc("京都府");               // bigrams 京都, 都府

        // Query "東京" → bigram 東京 → only the first doc has it.
        Search("東京", k: 10).Should().ContainSingle().Which.Should().Be(tokyo);
        // Query "京都" → bigram 京都 → both docs contain it.
        Search("京都", k: 10).Should().HaveCount(2);
    }

    [Fact]
    public void Deleted_node_is_not_returned()
    {
        var doc = AddDoc("secret content");
        Search("secret", k: 10).Should().ContainSingle().Which.Should().Be(doc);

        using (var tx = _db.BeginTransaction())
        {
            tx.DeleteNode(doc);
            tx.Commit();
        }

        Search("secret", k: 10).Should().BeEmpty();
    }

    [Fact]
    public void Search_composes_with_Out_traversal()
    {
        NodeId author;
        NodeId doc;
        using (var tx = _db.BeginTransaction())
        {
            author = tx.CreateNode("Author");
            doc = tx.CreateNode("Doc");
            tx.SetProperty(doc, "body", PropertyValue.FromString("quiver report"));
            tx.CreateRelationship(doc, author, "WROTE");
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var authors = rtx.G(_db.Schema).Search(Index, "quiver", k: 10).Out("WROTE").ToList();

        authors.Should().ContainSingle().Which.Should().Be(author);
    }
}

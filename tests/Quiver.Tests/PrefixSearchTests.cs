using FluentAssertions;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// GA-2 coverage: FTS prefix query support (<c>g.Search("idx", "quiv*", k)</c>).
/// Exercises wildcard detection, prefix expansion against the postings B+Tree,
/// and BM25 scoring across expanded terms on both the text-first and graph-first paths.
/// </summary>
public sealed class PrefixSearchTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public PrefixSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_prefix_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));
        _db.Schema.CreateFullTextIndex(Index, "Doc", "body");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private NodeId AddDoc(string body, string? lang = null)
    {
        using var tx = _db.BeginTransaction();
        var n = tx.CreateNode("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString(body));
        if (lang != null) tx.SetProperty(n, "lang", PropertyValue.FromString(lang));
        tx.Commit();
        return n;
    }

    [Fact]
    public void Prefix_query_matches_documents_containing_expanded_terms()
    {
        var n1 = AddDoc("quiver is a graph database");
        var n2 = AddDoc("quick brown fox");
        AddDoc("alpha beta gamma");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // "qui*" should expand to terms like "quiver", "quick", "qui" etc.
        var results = g.Search(Index, "qui*", k: 10).ToList();
        results.Should().Contain(n1);
        results.Should().Contain(n2);
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Prefix_query_returns_empty_when_no_terms_match()
    {
        AddDoc("quiver is a graph database");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var results = g.Search(Index, "zzz*", k: 10).ToList();
        results.Should().BeEmpty();
    }

    [Fact]
    public void Prefix_query_mixed_with_exact_term()
    {
        var n1 = AddDoc("quiver graph database engine");
        var n2 = AddDoc("quick graph search");
        AddDoc("alpha beta");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // "graph qui*" — exact "graph" + prefix "qui*"
        var results = g.Search(Index, "graph qui*", k: 10).ToList();
        results.Should().Contain(n1);
        results.Should().Contain(n2);
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Prefix_exact_match_includes_the_prefix_itself()
    {
        var n1 = AddDoc("graph database");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // "graph*" should match "graph" (the prefix itself is a valid term)
        var results = g.Search(Index, "graph*", k: 10).ToList();
        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Prefix_query_is_case_insensitive()
    {
        var n1 = AddDoc("Quiver is great");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // "QUIV*" should normalize to "quiv*" and match "quiver"
        var results = g.Search(Index, "QUIV*", k: 10).ToList();
        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Graph_first_prefix_query_matches_text_first()
    {
        AddDoc("quiver engine");
        AddDoc("quick search");
        AddDoc("alpha beta");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var textFirst = g.Search(Index, "qui*", k: 10).ToList();
        var graphFirst = g.Nodes().HasLabel("Doc").FilterByText(Index, "qui*", k: 10).ToList();

        graphFirst.Should().Equal(textFirst, "graph-first preserves text-first BM25 order for prefix queries");
    }

    [Fact]
    public void Prefix_with_stats_uses_wand_path()
    {
        AddDoc("quiver engine");
        AddDoc("quick fox");
        AddDoc("no match");

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema, stats);

        var withStats = g.Search(Index, "qui*", k: 10).ToList();
        withStats.Should().HaveCount(2);
    }

    [Fact]
    public void Bare_star_is_ignored()
    {
        AddDoc("hello world");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // A lone "*" should not match anything (no prefix)
        var results = g.Search(Index, "*", k: 10).ToList();
        results.Should().BeEmpty();
    }

    [Fact]
    public void Query_without_wildcard_is_unaffected()
    {
        var n1 = AddDoc("quiver engine");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var exact = g.Search(Index, "quiver", k: 10).ToList();
        exact.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Prefix_query_respects_k_limit()
    {
        for (int i = 0; i < 5; i++) AddDoc($"quiver document number {i}");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var results = g.Search(Index, "quiv*", k: 2).ToList();
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Prefix_query_with_HasLabel_pushdown()
    {
        AddDoc("quiver engine", lang: "en");
        AddDoc("quick search", lang: "ja");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var results = g.Search(Index, "qui*", k: 10).HasLabel("Doc").ToList();
        results.Should().HaveCount(2);
    }
}

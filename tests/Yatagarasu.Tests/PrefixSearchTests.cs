using FluentAssertions;
using Yatagarasu;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// 全文検索の前方一致クエリ (<c>g.Search("idx", "embed*", k)</c>) を検証する。
/// ワイルドカード検出、visible term dictionaryに対する接頭辞展開、展開語の BM25 採点を
/// text-first と graph-first の両経路で確認する。
/// </summary>
public sealed class PrefixSearchTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly YatagarasuDatabase _db;

    public PrefixSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_prefix_" + Guid.NewGuid().ToString("N"));
        _db = YatagarasuDatabase.Open(Path.Combine(_dir, "graph.yata"));
        _db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(Index, new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private VertexId AddDoc(string body, string? lang = null)
    {
        using var tx = _db.BeginWriteTransaction();
        var n = tx.CreateVertex("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString(body));
        if (lang != null) tx.SetProperty(n, "lang", PropertyValue.FromString(lang));
        tx.Commit();
        return n;
    }

    [Fact]
    public void Prefix_query_matches_documents_containing_expanded_terms()
    {
        var n1 = AddDoc("embedded yatagarasu graph database");
        var n2 = AddDoc("embedding search engine");
        AddDoc("alpha beta gamma");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // "embed*" expands to both "embedded" and "embedding".
        var results = g.Search(Index, "embed*", k: 10).ToList();
        results.Should().Contain(n1);
        results.Should().Contain(n2);
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Prefix_query_returns_empty_when_no_terms_match()
    {
        AddDoc("yatagarasu is a graph database");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var results = g.Search(Index, "zzz*", k: 10).ToList();
        results.Should().BeEmpty();
    }

    [Fact]
    public void Prefix_query_mixed_with_exact_term()
    {
        var n1 = AddDoc("embedded yatagarasu graph database engine");
        var n2 = AddDoc("embedding graph search");
        AddDoc("alpha beta");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // "graph embed*" — exact "graph" + prefix "embed*"
        var results = g.Search(Index, "graph embed*", k: 10).ToList();
        results.Should().Contain(n1);
        results.Should().Contain(n2);
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Prefix_exact_match_includes_the_prefix_itself()
    {
        var n1 = AddDoc("graph database");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // "graph*" should match "graph" (the prefix itself is a valid term)
        var results = g.Search(Index, "graph*", k: 10).ToList();
        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Prefix_query_is_case_insensitive()
    {
        var n1 = AddDoc("Embedded is great");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // "EMBE*" should normalize to "embe*" and match "embedded".
        var results = g.Search(Index, "EMBE*", k: 10).ToList();
        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Graph_first_prefix_query_matches_text_first()
    {
        AddDoc("embedded yatagarasu engine");
        AddDoc("embedding search");
        AddDoc("alpha beta");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var textFirst = g.Search(Index, "embed*", k: 10).ToList();
        var graphFirst = g.Vertices().HasLabel("Doc").FilterByText(Index, "embed*", k: 10).ToList();

        graphFirst.Should().Equal(textFirst, "graph-first preserves text-first BM25 order for prefix queries");
    }

    [Fact]
    public void Prefix_with_stats_uses_wand_path()
    {
        AddDoc("embedded yatagarasu engine");
        AddDoc("embedding search engine");
        AddDoc("no match");

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query.WithStats(stats);

        var withStats = g.Search(Index, "embed*", k: 10).ToList();
        withStats.Should().HaveCount(2);
    }

    [Fact]
    public void Bare_star_is_ignored()
    {
        AddDoc("hello world");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // A lone "*" should not match anything (no prefix)
        var results = g.Search(Index, "*", k: 10).ToList();
        results.Should().BeEmpty();
    }

    [Fact]
    public void Query_without_wildcard_is_unaffected()
    {
        var n1 = AddDoc("yatagarasu engine");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var exact = g.Search(Index, "yatagarasu", k: 10).ToList();
        exact.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Prefix_query_respects_k_limit()
    {
        for (int i = 0; i < 5; i++) AddDoc($"embedded document number {i}");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var results = g.Search(Index, "embed*", k: 2).ToList();
        results.Should().HaveCount(2);
    }

    [Fact]
    public void Prefix_query_with_HasLabel_pushdown()
    {
        AddDoc("embedded yatagarasu engine", lang: "en");
        AddDoc("embedding search", lang: "ja");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var results = g.Search(Index, "embed*", k: 10).HasLabel("Doc").ToList();
        results.Should().HaveCount(2);
    }
}

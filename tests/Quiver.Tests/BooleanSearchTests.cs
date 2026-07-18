using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 全文検索の論理クエリ (AND、OR、NOT) を検証する。
/// 再帰下降パーサー、BM25 スコアラーの論理フィルター、
/// text-first と graph-first の両演算子経路との統合を対象とする。
/// </summary>
public sealed class BooleanSearchTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public BooleanSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_bool_" + Guid.NewGuid().ToString("N"));
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

    private List<VertexId> Search(string query, int k = 10)
    {
        using var rtx = _db.BeginReadTransaction();
        return rtx.Query.Search(Index, query, k).ToList();
    }

    // ── AND ──

    [Fact]
    public void And_returns_only_documents_matching_all_required_terms()
    {
        var both = AddDoc("graph database engine");
        AddDoc("graph visualization tool");
        AddDoc("database migration tool");

        var results = Search("graph AND database");

        results.Should().ContainSingle().Which.Should().Be(both);
    }

    [Fact]
    public void And_chain_requires_all_terms()
    {
        var all = AddDoc("graph database engine");
        AddDoc("graph database tool");
        AddDoc("graph engine");

        var results = Search("graph AND database AND engine");

        results.Should().ContainSingle().Which.Should().Be(all);
    }

    // ── NOT ──

    [Fact]
    public void Not_excludes_matching_documents()
    {
        var graphOnly = AddDoc("graph visualization");
        AddDoc("graph database engine");

        var results = Search("graph NOT database");

        results.Should().ContainSingle().Which.Should().Be(graphOnly);
    }

    [Fact]
    public void Not_without_positive_terms_returns_empty()
    {
        AddDoc("graph database");

        var results = Search("NOT graph");

        results.Should().BeEmpty("pure NOT query has no positive terms to score");
    }

    // ── OR (explicit) ──

    [Fact]
    public void Or_returns_documents_matching_any_term()
    {
        var d1 = AddDoc("graph visualization");
        var d2 = AddDoc("database migration");
        AddDoc("unrelated content");

        var results = Search("graph OR database");

        results.Should().HaveCount(2);
        results.Should().Contain(d1);
        results.Should().Contain(d2);
    }

    [Fact]
    public void Explicit_or_is_equivalent_to_implicit_or()
    {
        AddDoc("graph visualization");
        AddDoc("database migration");
        AddDoc("unrelated content");

        var explicitOr = Search("graph OR database");
        var implicitOr = Search("graph database");

        explicitOr.Should().BeEquivalentTo(implicitOr);
    }

    // ── Combined ──

    [Fact]
    public void And_with_not_filters_correctly()
    {
        var match = AddDoc("graph database engine");
        AddDoc("graph database vector store");
        AddDoc("graph engine only");

        var results = Search("graph AND database NOT vector");

        results.Should().ContainSingle().Which.Should().Be(match);
    }

    [Fact]
    public void And_or_not_combined()
    {
        var d1 = AddDoc("graph database engine");
        var d2 = AddDoc("graph search engine");
        AddDoc("graph vector store");

        // graph is required, "engine" is optional (boosts score), "vector" is excluded
        var results = Search("graph AND engine NOT vector");

        results.Should().HaveCount(2);
        results.Should().Contain(d1);
        results.Should().Contain(d2);
    }

    // ── Prefix + Boolean ──

    [Fact]
    public void Prefix_with_and()
    {
        var match = AddDoc("quiver database engine");
        AddDoc("quiver visualization tool");
        AddDoc("database only");

        var results = Search("quiv* AND database");

        results.Should().ContainSingle().Which.Should().Be(match);
    }

    [Fact]
    public void Prefix_with_not()
    {
        var match = AddDoc("quiver engine");
        AddDoc("quiver database");

        var results = Search("quiv* NOT database");

        results.Should().ContainSingle().Which.Should().Be(match);
    }

    // ── Operator case sensitivity ──

    [Fact]
    public void Lowercase_and_or_not_are_treated_as_search_terms()
    {
        var d1 = AddDoc("this and that");
        var d2 = AddDoc("sink or swim");
        var d3 = AddDoc("not today");

        // lowercase "and" / "or" / "not" are not operators — they're search terms
        Search("and").Should().Contain(d1);
        Search("or").Should().Contain(d2);
        Search("not").Should().Contain(d3);
    }

    // ── k limit ──

    [Fact]
    public void And_respects_k_limit()
    {
        for (int i = 0; i < 5; i++)
            AddDoc($"graph database document {i}");

        var results = Search("graph AND database", k: 2);

        results.Should().HaveCount(2);
    }

    // ── graph-first path ──

    [Fact]
    public void Graph_first_boolean_matches_text_first()
    {
        AddDoc("graph database engine");
        AddDoc("graph visualization tool");
        AddDoc("database only");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var textFirst = g.Search(Index, "graph AND database", k: 10).ToList();
        var graphFirst = g.Vertices().HasLabel("Doc")
            .FilterByText(Index, "graph AND database", k: 10).ToList();

        graphFirst.Should().Equal(textFirst);
    }

    [Fact]
    public void Graph_first_not_matches_text_first()
    {
        AddDoc("graph database");
        AddDoc("graph engine");
        AddDoc("unrelated");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var textFirst = g.Search(Index, "graph NOT database", k: 10).ToList();
        var graphFirst = g.Vertices().HasLabel("Doc")
            .FilterByText(Index, "graph NOT database", k: 10).ToList();

        graphFirst.Should().Equal(textFirst);
    }

    // ── Stats path ──

    [Fact]
    public void Boolean_query_with_stats()
    {
        var match = AddDoc("graph database engine");
        AddDoc("graph visualization tool");
        AddDoc("database only");

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query.WithStats(stats);

        var results = g.Search(Index, "graph AND database", k: 10).ToList();

        results.Should().ContainSingle().Which.Should().Be(match);
    }

    // ── Edge cases ──

    [Fact]
    public void Trailing_and_is_ignored()
    {
        var d1 = AddDoc("graph engine");

        // "graph AND" with nothing after AND → "graph" is promoted to required, no second operand
        var results = Search("graph AND");

        results.Should().ContainSingle().Which.Should().Be(d1);
    }

    [Fact]
    public void Leading_not_with_positive_term()
    {
        AddDoc("graph database");
        var d2 = AddDoc("graph engine");

        var results = Search("NOT database graph");

        // "database" excluded, "graph" optional → only d2
        results.Should().ContainSingle().Which.Should().Be(d2);
    }

    [Fact]
    public void Multiple_not_clauses()
    {
        var d1 = AddDoc("graph engine");
        AddDoc("graph database");
        AddDoc("graph vector");

        var results = Search("graph NOT database NOT vector");

        results.Should().ContainSingle().Which.Should().Be(d1);
    }

    [Fact]
    public void Deleted_vertex_is_not_returned_with_boolean()
    {
        var d1 = AddDoc("graph database engine");
        AddDoc("graph visualization");

        using (var tx = _db.BeginWriteTransaction())
        {
            tx.DeleteVertex(d1);
            tx.Commit();
        }

        var results = Search("graph AND database");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Read_your_own_writes_with_boolean()
    {
        using var tx = _db.BeginWriteTransaction();
        var n = tx.CreateVertex("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString("graph database engine"));

        var hits = tx.Query.Search(Index, "graph AND database", k: 10).ToList();
        hits.Should().ContainSingle().Which.Should().Be(n);

        tx.Commit();
    }
}

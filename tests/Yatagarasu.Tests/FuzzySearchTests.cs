using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// 全文検索のあいまいクエリ (<c>g.Search("idx", "yatagarasu~1", k)</c>) を検証する。
/// レーベンシュタイン距離による展開、パーサーによる <c>~N</c> の認識、
/// BM25 採点、論理演算子、graph-first 経路との統合を対象とする。
/// </summary>
public sealed class FuzzySearchTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly YatagarasuDatabase _db;

    public FuzzySearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_fuzzy_" + Guid.NewGuid().ToString("N"));
        _db = YatagarasuDatabase.Open(Path.Combine(_dir, "graph.yata"));
        _db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(Index, new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
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

    // ── Basic fuzzy ──

    [Fact]
    public void Fuzzy_matches_edit_distance_1_by_default()
    {
        var n1 = AddDoc("yatagarasu is a graph database");
        AddDoc("alpha beta gamma");

        // "yatagaras~" (missing 'u') with default edit distance 1 should match "yatagarasu"
        var results = Search("yatagaras~");

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Fuzzy_with_explicit_distance_1()
    {
        var n1 = AddDoc("yatagarasu is great");
        AddDoc("alpha beta gamma");

        // "yatagaras~1" — one deletion
        var results = Search("yatagaras~1");

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Fuzzy_with_substitution()
    {
        var n1 = AddDoc("graph database engine");
        AddDoc("alpha beta gamma");

        // "grath~1" → "graph" (substitution of 'a'→'p' at position 2... wait, actually "grath" vs "graph":
        // g-r-a-t-h vs g-r-a-p-h → one substitution (t→p))
        var results = Search("grath~1");

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Fuzzy_with_insertion()
    {
        var n1 = AddDoc("graph database engine");
        AddDoc("alpha beta gamma");

        // "grph~1" → "graph" (insertion of 'a')
        var results = Search("grph~1");

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Fuzzy_distance_2_matches_two_edits()
    {
        var n1 = AddDoc("database engine");
        AddDoc("alpha beta gamma");

        // "dtabase~2" → "database" (two edits: missing 'a' at pos 1 + extra 't')
        // Actually "dtabase" vs "database": d-t-a-b-a-s-e vs d-a-t-a-b-a-s-e
        // That's a transposition-like pattern. Let me use a clearer example.
        // "databse~2" (missing 'a', swapped 's'→'s': one deletion) — actually that's dist 1.
        // Use: "dttabase~2" vs "database" → d-t-t-a-b-a-s-e vs d-a-t-a-b-a-s-e → dist 2 (subst t→a, extra t)
        var results = Search("dtabse~2");

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Fuzzy_does_not_match_beyond_max_distance()
    {
        AddDoc("yatagarasu is a graph database");

        // "xyz~1" has no indexed term within edit distance 1
        var results = Search("xyz~1");

        results.Should().BeEmpty();
    }

    [Fact]
    public void Fuzzy_distance_0_is_exact_match()
    {
        var n1 = AddDoc("graph database");
        AddDoc("alpha beta");

        // ~0 means exact match — only "graph" itself
        var results = Search("graph~0");

        // ~0 is capped to 0 which means no fuzzy expansion; falls through to exact
        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Fuzzy_matches_multiple_terms()
    {
        var n1 = AddDoc("yatagarasu graph engine");
        var n2 = AddDoc("quick brown fox");
        AddDoc("alpha beta gamma");

        // "yatagaras~1" should expand to "yatagarasu" (edit dist 1), but not "quick".
        var results = Search("yatagaras~1");

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    // ── Fuzzy with exact terms ──

    [Fact]
    public void Fuzzy_mixed_with_exact_term()
    {
        var n1 = AddDoc("yatagarasu graph database");
        var n2 = AddDoc("yatagarasu search engine");
        AddDoc("alpha beta gamma");

        // Fuzzy "grph~1" expands to "graph"; plus exact "yatagarasu"
        var results = Search("grph~1 yatagarasu");

        results.Should().HaveCount(2);
        results.Should().Contain(n1);
        results.Should().Contain(n2);
    }

    // ── Fuzzy with Boolean operators ──

    [Fact]
    public void Fuzzy_with_and()
    {
        var match = AddDoc("yatagarasu database engine");
        AddDoc("yatagarasu visualization tool");
        AddDoc("database only");

        var results = Search("yatagaras~1 AND database");

        results.Should().ContainSingle().Which.Should().Be(match);
    }

    [Fact]
    public void Fuzzy_with_not()
    {
        var match = AddDoc("yatagarasu engine");
        AddDoc("yatagarasu database");

        var results = Search("yatagaras~1 NOT database");

        results.Should().ContainSingle().Which.Should().Be(match);
    }

    [Fact]
    public void Fuzzy_with_and_or_not_combined()
    {
        var d1 = AddDoc("yatagarasu database engine");
        var d2 = AddDoc("yatagarasu search engine");
        AddDoc("yatagarasu vector store");

        var results = Search("yatagaras~1 AND engine NOT vector");

        results.Should().HaveCount(2);
        results.Should().Contain(d1);
        results.Should().Contain(d2);
    }

    // ── Graph-first path ──

    [Fact]
    public void Graph_first_fuzzy_matches_text_first()
    {
        AddDoc("yatagarasu database engine");
        AddDoc("yatagarasu visualization tool");
        AddDoc("unrelated content");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var textFirst = g.Search(Index, "yatagaras~1", k: 10).ToList();
        var graphFirst = g.Vertices().HasLabel("Doc")
            .FilterByText(Index, "yatagaras~1", k: 10).ToList();

        graphFirst.Should().Equal(textFirst);
    }

    // ── Stats path ──

    [Fact]
    public void Fuzzy_with_stats()
    {
        var n1 = AddDoc("yatagarasu database engine");
        AddDoc("alpha beta gamma");

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query.WithStats(stats);

        var results = g.Search(Index, "yatagaras~1", k: 10).ToList();

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    // ── k limit ──

    [Fact]
    public void Fuzzy_respects_k_limit()
    {
        for (int i = 0; i < 5; i++)
            AddDoc($"yatagarasu document {i}");

        var results = Search("yatagaras~1", k: 2);

        results.Should().HaveCount(2);
    }

    // ── Edge cases ──

    [Fact]
    public void Tilde_without_preceding_word_is_ignored()
    {
        var n1 = AddDoc("graph database");

        // standalone "~1" has no word to attach to; "graph" is an exact term
        var results = Search("~1 graph");

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Distance_capped_at_2()
    {
        var n1 = AddDoc("graph database");
        AddDoc("alpha beta gamma");

        // "~9" should be capped to 2
        // "grp~9" → normalized to "grp~2"; "graph" is edit distance 2 from "grp" (add 'a' + add 'h')
        var results = Search("grp~9");

        results.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Deleted_vertex_is_not_returned_with_fuzzy()
    {
        var d1 = AddDoc("yatagarasu database engine");
        AddDoc("yatagarasu visualization");

        using (var tx = _db.BeginWriteTransaction())
        {
            tx.DeleteVertex(d1);
            tx.Commit();
        }

        var results = Search("yatagaras~1 AND database");

        results.Should().BeEmpty();
    }

    [Fact]
    public void Read_your_own_writes_with_fuzzy()
    {
        using var tx = _db.BeginWriteTransaction();
        var n = tx.CreateVertex("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString("yatagarasu database engine"));

        var hits = tx.Query.Search(Index, "yatagaras~1", k: 10).ToList();
        hits.Should().ContainSingle().Which.Should().Be(n);

        tx.Commit();
    }
}

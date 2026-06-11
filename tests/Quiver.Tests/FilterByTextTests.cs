using FluentAssertions;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FTS-4 coverage: the graph-first full-text path. Exercises the
/// <c>.FilterByText(...)</c> DSL, the optimizer's FullTextPushdown rewrite
/// (<c>g.Search(...).HasLabel/Has(...)</c> → graph-first <see cref="FullTextScanOp"/>
/// with <c>Candidate != null</c>), the statistics-aware text-first fallback, and the
/// BM25 corpus snapshot collected into <see cref="GraphStats"/>.
/// </summary>
public sealed class FilterByTextTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public FilterByTextTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts4_" + Guid.NewGuid().ToString("N"));
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

    private void AddOther(int count)
    {
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < count; i++) tx.CreateNode("Author");
        tx.Commit();
    }

    // ── result equivalence ──────────────────────────────────────────────────────

    [Fact]
    public void Graph_first_ranking_matches_text_first_when_no_divergence()
    {
        // Only Doc nodes are indexed (index is bound to Doc/body), so the global BM25
        // ranking IS the Doc ranking. Graph-first over the Doc candidate set must return
        // the identical sequence (completion condition: "結果が text-first と一致, 順位含む").
        AddDoc("quiver");                 // tf=1
        AddDoc("quiver quiver");          // tf=2
        AddDoc("quiver quiver quiver");   // tf=3  → strict BM25 order, no ties
        AddOther(3);                      // non-indexed nodes, must not perturb either path

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var textFirst  = g.Search(Index, "quiver", k: 10).ToList();
        var graphFirst = g.Nodes().HasLabel("Doc").FilterByText(Index, "quiver", k: 10).ToList();

        graphFirst.Should().Equal(textFirst, "graph-first preserves the text-first BM25 order");
        graphFirst.Should().HaveCount(3);
    }

    [Fact]
    public void Search_HasLabel_pushdown_returns_same_as_manual_FilterByText()
    {
        AddDoc("alpha");
        AddDoc("alpha alpha");
        AddOther(2);

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var pushdown = g.Search(Index, "alpha", k: 10).HasLabel("Doc").ToList();
        var manual   = g.Nodes().HasLabel("Doc").FilterByText(Index, "alpha", k: 10).ToList();

        pushdown.Should().Equal(manual);
    }

    [Fact]
    public void Has_subfilter_pushdown_returns_full_k_when_text_first_would_starve()
    {
        // 5 high-tf English Docs (lang=en) + 2 low-tf Docs (lang=ja), all matching "quiver".
        // text-first top-2 = 2 en Docs, then Has(lang=ja) drops both → 0.
        // Push-down filters lang=ja first → 2 ja Docs survive (no k starvation).
        for (int i = 0; i < 5; i++) AddDoc("quiver quiver", lang: "en");
        for (int i = 0; i < 2; i++) AddDoc("quiver", lang: "ja");

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var result = g.Search(Index, "quiver", k: 2).Has("lang", "ja").ToList();

        result.Should().HaveCount(2);
    }

    [Fact]
    public void FilterByText_composes_with_Out_traversal()
    {
        NodeId author;
        using (var tx = _db.BeginTransaction())
        {
            author = tx.CreateNode("Author");
            var doc = tx.CreateNode("Doc");
            tx.SetProperty(doc, "body", PropertyValue.FromString("quiver report"));
            tx.CreateRelationship(doc, author, "WROTE");
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var authors = rtx.G(_db.Schema)
            .Nodes().HasLabel("Doc")
            .FilterByText(Index, "quiver", k: 10)
            .Out("WROTE").ToList();

        authors.Should().ContainSingle().Which.Should().Be(author);
    }

    [Fact]
    public void FilterByText_with_zero_candidates_returns_empty()
    {
        AddDoc("quiver report");

        using var rtx = _db.BeginReadOnlyTransaction();
        var result = rtx.G(_db.Schema)
            .Nodes().HasLabel("Ghost")   // no such label → empty candidate set
            .FilterByText(Index, "quiver", k: 10).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterByText_with_missing_index_throws_like_text_first()
    {
        // Symmetric with the text-first operator: a non-existent index throws up-front,
        // not a silent empty result, even though the candidate set is non-empty.
        AddDoc("quiver report");

        using var rtx = _db.BeginReadOnlyTransaction();
        var act = () => rtx.G(_db.Schema)
            .Nodes().HasLabel("Doc")
            .FilterByText("idx_missing", "quiver", k: 10).ToList();

        act.Should().Throw<ConstraintException>();
    }

    [Fact]
    public void Deleted_node_is_not_returned_via_graph_first()
    {
        var doc = AddDoc("secret content");
        using var rtx0 = _db.BeginReadOnlyTransaction();
        rtx0.G(_db.Schema).Nodes().HasLabel("Doc").FilterByText(Index, "secret", k: 10)
            .ToList().Should().ContainSingle().Which.Should().Be(doc);

        using (var tx = _db.BeginTransaction()) { tx.DeleteNode(doc); tx.Commit(); }

        using var rtx = _db.BeginReadOnlyTransaction();
        rtx.G(_db.Schema).Nodes().HasLabel("Doc").FilterByText(Index, "secret", k: 10)
            .ToList().Should().BeEmpty();
    }

    // ── plan regression (optimizer rewrite shape) ───────────────────────────────

    [Fact]
    public void Search_no_filter_chain_stays_text_first()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var optimized = rtx.G(_db.Schema).Search(Index, "quiver", k: 5).Optimized();

        optimized.Should().BeOfType<FullTextScanOp>();
        ((FullTextScanOp)optimized).Candidate.Should().BeNull("no filter → text-first");
    }

    [Fact]
    public void Search_HasLabel_materializes_to_graph_first_without_stats()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var optimized = rtx.G(_db.Schema).Search(Index, "quiver", k: 5).HasLabel("Doc").Optimized();

        optimized.Should().BeOfType<FullTextScanOp>();
        ((FullTextScanOp)optimized).Candidate.Should().NotBeNull("filter chain → graph-first");
    }

    [Fact]
    public void HighLabelCardinality_falls_back_to_text_first_when_stats_present()
    {
        // 50 Doc + 50 Author → Doc cardinality = 50% >= 30% threshold → text-first.
        for (int i = 0; i < 50; i++) AddDoc("quiver");
        AddOther(50);

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadOnlyTransaction();
        var optimized = rtx.G(_db.Schema, stats).Search(Index, "quiver", k: 5).HasLabel("Doc").Optimized();

        optimized.Should().BeOfType<FilterOp>("50% >= 30% → stay text-first (filter on top)");
    }

    [Fact]
    public void LowLabelCardinality_keeps_graph_first_with_stats()
    {
        // 5 Doc + 95 Author → Doc cardinality = 5% < 30% → graph-first.
        for (int i = 0; i < 5; i++) AddDoc("quiver");
        AddOther(95);

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadOnlyTransaction();
        var optimized = rtx.G(_db.Schema, stats).Search(Index, "quiver", k: 5).HasLabel("Doc").Optimized();

        optimized.Should().BeOfType<FullTextScanOp>();
        ((FullTextScanOp)optimized).Candidate.Should().NotBeNull("5% < 30% → graph-first");
    }

    [Fact]
    public void Text_first_fallback_produces_same_results_as_graph_first()
    {
        // Doc-bound index means HasLabel("Doc") keeps every indexed hit, so the
        // text-first fallback (50% card) and graph-first (no stats) yield the same set.
        long[] docIds;
        using (var tx = _db.BeginTransaction())
        {
            docIds = new long[50];
            for (int i = 0; i < 50; i++)
            {
                var d = tx.CreateNode("Doc");
                docIds[i] = d.Value;
                tx.SetProperty(d, "body", PropertyValue.FromString("quiver " + (i % 3)));
            }
            for (int i = 0; i < 50; i++) tx.CreateNode("Author");
            tx.Commit();
        }

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadOnlyTransaction();

        var withStats    = rtx.G(_db.Schema, stats).Search(Index, "quiver", k: 50).HasLabel("Doc").ToList();
        var withoutStats = rtx.G(_db.Schema).Search(Index, "quiver", k: 50).HasLabel("Doc").ToList();

        withStats.Select(n => n.Value).Should().BeEquivalentTo(withoutStats.Select(n => n.Value));
        withStats.Should().OnlyContain(n => docIds.Contains(n.Value));
    }

    [Fact]
    public void Limit_smaller_than_k_shrinks_graph_first_k()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var optimized = rtx.G(_db.Schema)
            .Search(Index, "quiver", k: 20).HasLabel("Doc").Limit(5).Optimized();

        optimized.Should().BeOfType<FullTextScanOp>();
        var ft = (FullTextScanOp)optimized;
        ft.K.Should().Be(5);
        ft.Candidate.Should().NotBeNull("HasLabel → graph-first, Limit shrinks k");
    }

    // ── GraphStats corpus snapshot ──────────────────────────────────────────────

    [Fact]
    public void CollectStats_snapshots_fulltext_corpus()
    {
        AddDoc("a b c");   // docLen 3
        AddDoc("d e");     // docLen 2

        var stats = _db.CollectStats();
        var corpus = stats.FullTextCorpus(Index);

        corpus.Should().NotBeNull();
        corpus!.Value.DocumentCount.Should().Be(2);
        corpus.Value.AverageDocLength.Should().BeApproximately(2.5, 1e-9);
    }
}

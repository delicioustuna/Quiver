using FluentAssertions;
using Yatagarasu;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Query.Logical;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// graph-first の全文検索経路を検証する。
/// <c>.FilterByText(...)</c> DSL、オプティマイザーによる FullTextPushdown、
/// 統計に基づく text-first へのフォールバック、
/// <see cref="GraphStats"/> に収集される BM25 コーパススナップショットを対象とする。
/// </summary>
public sealed class FilterByTextTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly YatagarasuDatabase _db;

    public FilterByTextTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_fts4_" + Guid.NewGuid().ToString("N"));
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

    private void AddOther(int count)
    {
        using var tx = _db.BeginWriteTransaction();
        for (int i = 0; i < count; i++) tx.CreateVertex("Author");
        tx.Commit();
    }

    // ── 結果の同値性 ───────────────────────────────────────────────────────────

    [Fact]
    public void Graph_first_ranking_matches_text_first_when_no_divergence()
    {
        // インデックスは Doc/body に結び付くため、全体の BM25 順位が Doc の順位になる。
        // Doc 候補集合に対する graph-first も同じ順序を返す必要がある。
        AddDoc("yatagarasu");                 // tf=1
        AddDoc("yatagarasu yatagarasu");          // tf=2
        AddDoc("yatagarasu yatagarasu yatagarasu");   // tf=3  → strict BM25 order, no ties
        AddOther(3);                      // non-indexed vertices, must not perturb either path

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var textFirst  = g.Search(Index, "yatagarasu", k: 10).ToList();
        var graphFirst = g.Vertices().HasLabel("Doc").FilterByText(Index, "yatagarasu", k: 10).ToList();

        graphFirst.Should().Equal(textFirst, "graph-first preserves the text-first BM25 order");
        graphFirst.Should().HaveCount(3);
    }

    [Fact]
    public void Search_HasLabel_pushdown_returns_same_as_manual_FilterByText()
    {
        AddDoc("alpha");
        AddDoc("alpha alpha");
        AddOther(2);

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var pushdown = g.Search(Index, "alpha", k: 10).HasLabel("Doc").ToList();
        var manual   = g.Vertices().HasLabel("Doc").FilterByText(Index, "alpha", k: 10).ToList();

        pushdown.Should().Equal(manual);
    }

    [Fact]
    public void Has_subfilter_pushdown_returns_full_k_when_text_first_would_starve()
    {
        // 5 high-tf English Docs (lang=en) + 2 low-tf Docs (lang=ja), all matching "yatagarasu".
        // text-first の上位 2 件は英語文書なので、Has(lang=ja) で両方を除外すると 0 件になる。
        // Push-down filters lang=ja first → 2 ja Docs survive (no k starvation).
        for (int i = 0; i < 5; i++) AddDoc("yatagarasu yatagarasu", lang: "en");
        for (int i = 0; i < 2; i++) AddDoc("yatagarasu", lang: "ja");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var result = g.Search(Index, "yatagarasu", k: 2).Has("lang", "ja").ToList();

        result.Should().HaveCount(2);
    }

    [Fact]
    public void FilterByText_composes_with_Out_traversal()
    {
        VertexId author;
        using (var tx = _db.BeginWriteTransaction())
        {
            author = tx.CreateVertex("Author");
            var doc = tx.CreateVertex("Doc");
            tx.SetProperty(doc, "body", PropertyValue.FromString("yatagarasu report"));
            tx.CreateEdge(doc, author, "WROTE");
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var authors = rtx.Query
            .Vertices().HasLabel("Doc")
            .FilterByText(Index, "yatagarasu", k: 10)
            .Out("WROTE").ToList();

        authors.Should().ContainSingle().Which.Should().Be(author);
    }

    [Fact]
    public void FilterByText_with_zero_candidates_returns_empty()
    {
        AddDoc("yatagarasu report");

        using var rtx = _db.BeginReadTransaction();
        var result = rtx.Query
            .Vertices().HasLabel("Ghost")   // no such label → empty candidate set
            .FilterByText(Index, "yatagarasu", k: 10).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterByText_with_missing_index_throws_like_text_first()
    {
        // Symmetric with the text-first operator: a non-existent index throws up-front,
        // not a silent empty result, even though the candidate set is non-empty.
        AddDoc("yatagarasu report");

        using var rtx = _db.BeginReadTransaction();
        var act = () => rtx.Query
            .Vertices().HasLabel("Doc")
            .FilterByText("idx_missing", "yatagarasu", k: 10).ToList();

        act.Should().Throw<ConstraintException>();
    }

    [Fact]
    public void Deleted_vertex_is_not_returned_via_graph_first()
    {
        var doc = AddDoc("secret content");
        using var rtx0 = _db.BeginReadTransaction();
        rtx0.Query.Vertices().HasLabel("Doc").FilterByText(Index, "secret", k: 10)
            .ToList().Should().ContainSingle().Which.Should().Be(doc);

        using (var tx = _db.BeginWriteTransaction()) { tx.DeleteVertex(doc); tx.Commit(); }

        using var rtx = _db.BeginReadTransaction();
        rtx.Query.Vertices().HasLabel("Doc").FilterByText(Index, "secret", k: 10)
            .ToList().Should().BeEmpty();
    }

    // ── plan regression (optimizer rewrite shape) ───────────────────────────────

    [Fact]
    public void Search_no_filter_chain_stays_text_first()
    {
        using var rtx = _db.BeginReadTransaction();
        var optimized = rtx.Query.Search(Index, "yatagarasu", k: 5).Optimized();

        optimized.Should().BeOfType<FullTextScanOp>();
        ((FullTextScanOp)optimized).Candidate.Should().BeNull("no filter → text-first");
    }

    [Fact]
    public void Search_HasLabel_materializes_to_graph_first_without_stats()
    {
        using var rtx = _db.BeginReadTransaction();
        var optimized = rtx.Query.Search(Index, "yatagarasu", k: 5).HasLabel("Doc").Optimized();

        optimized.Should().BeOfType<FullTextScanOp>();
        ((FullTextScanOp)optimized).Candidate.Should().NotBeNull("filter chain → graph-first");
    }

    [Fact]
    public void HighLabelCardinality_falls_back_to_text_first_when_stats_present()
    {
        // 50 Doc + 50 Author → Doc cardinality = 50% >= 30% threshold → text-first.
        for (int i = 0; i < 50; i++) AddDoc("yatagarasu");
        AddOther(50);

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadTransaction();
        var optimized = rtx.Query.WithStats(stats).Search(Index, "yatagarasu", k: 5).HasLabel("Doc").Optimized();

        optimized.Should().BeOfType<FilterOp>("50% >= 30% → stay text-first (filter on top)");
    }

    [Fact]
    public void LowLabelCardinality_keeps_graph_first_with_stats()
    {
        // 5 Doc + 95 Author → Doc cardinality = 5% < 30% → graph-first.
        for (int i = 0; i < 5; i++) AddDoc("yatagarasu");
        AddOther(95);

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadTransaction();
        var optimized = rtx.Query.WithStats(stats).Search(Index, "yatagarasu", k: 5).HasLabel("Doc").Optimized();

        optimized.Should().BeOfType<FullTextScanOp>();
        ((FullTextScanOp)optimized).Candidate.Should().NotBeNull("5% < 30% → graph-first");
    }

    [Fact]
    public void Text_first_fallback_produces_same_results_as_graph_first()
    {
        // Doc-bound index means HasLabel("Doc") keeps every indexed hit, so the
        // text-first fallback (50% card) and graph-first (no stats) yield the same set.
        long[] docIds;
        using (var tx = _db.BeginWriteTransaction())
        {
            docIds = new long[50];
            for (int i = 0; i < 50; i++)
            {
                var d = tx.CreateVertex("Doc");
                docIds[i] = d.Value;
                tx.SetProperty(d, "body", PropertyValue.FromString("yatagarasu " + (i % 3)));
            }
            for (int i = 0; i < 50; i++) tx.CreateVertex("Author");
            tx.Commit();
        }

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadTransaction();

        var withStats    = rtx.Query.WithStats(stats).Search(Index, "yatagarasu", k: 50).HasLabel("Doc").ToList();
        var withoutStats = rtx.Query.Search(Index, "yatagarasu", k: 50).HasLabel("Doc").ToList();

        withStats.Select(n => n.Value).Should().BeEquivalentTo(withoutStats.Select(n => n.Value));
        withStats.Should().OnlyContain(n => docIds.Contains(n.Value));
    }

    [Fact]
    public void Limit_smaller_than_k_shrinks_graph_first_k()
    {
        using var rtx = _db.BeginReadTransaction();
        var optimized = rtx.Query
            .Search(Index, "yatagarasu", k: 20).HasLabel("Doc").Limit(5).Optimized();

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

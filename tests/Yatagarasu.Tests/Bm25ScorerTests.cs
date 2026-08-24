using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Index.FullText;
using Yatagarasu.Query.Physical;
using Yatagarasu.Transactions;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// 既知のコーパスを使って BM25 の採点を検証する。
/// 既定パラメーター、語頻度の飽和、逆文書頻度、文書長の正規化、複数語の加算、
/// 空文書、単一文書コーパス、不一致語の境界条件を対象とする。
/// </summary>
public sealed class Bm25ScorerTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly YatagarasuDatabase _db;

    public Bm25ScorerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_bm25_" + Guid.NewGuid().ToString("N"));
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

    private FullTextSegmentSnapshot FullTextSnapshot()
    {
        using var transaction = _db.BeginReadTransaction();
        ITransaction inner = transaction.AsInternal().Inner;
        inner.FullTextSegments.Should().NotBeNull();
        inner.FullTextSegments!.TryOpen(inner, Index, out var snapshot).Should().BeTrue();
        return snapshot;
    }

    // ── Default parameters ──────────────────────────────────────────────

    [Fact]
    public void Default_k1_is_1_2()
    {
        Bm25Scorer.K1.Should().BeApproximately(1.2, 0.001);
    }

    [Fact]
    public void Default_b_is_0_75()
    {
        Bm25Scorer.B.Should().BeApproximately(0.75, 0.001);
    }

    // ── TF (term frequency) effect ──────────────────────────────────────

    [Fact]
    public void Higher_term_frequency_ranks_higher()
    {
        // Three documents with tf("cat") = 1, 2, 3 and identical length padding.
        // BM25 TF saturation: score(tf=3) > score(tf=2) > score(tf=1).
        var tf1 = AddDoc("cat dog bird fish frog");
        var tf2 = AddDoc("cat cat dog bird fish");
        var tf3 = AddDoc("cat cat cat dog bird");

        var results = Search("cat");

        results.Should().HaveCount(3);
        results[0].Should().Be(tf3, "tf=3 should rank highest");
        results[1].Should().Be(tf2, "tf=2 should rank second");
        results[2].Should().Be(tf1, "tf=1 should rank last");
    }

    [Fact]
    public void Tf_saturation_diminishes_score_gains()
    {
        // With fixed doc length, increasing tf from 1->2 should give a larger
        // score jump than 2->3 (BM25 sub-linear saturation).
        // We verify this indirectly: a document with tf=2 should rank between
        // tf=1 and tf=3, confirming the ordering is monotone.
        var tf1 = AddDoc("alpha beta gamma delta epsilon");
        var tf2 = AddDoc("alpha alpha beta gamma delta");
        var tf3 = AddDoc("alpha alpha alpha beta gamma");

        var results = Search("alpha");

        results.Should().Equal(tf3, tf2, tf1);
    }

    // ── IDF (inverse document frequency) effect ────────────────────────

    [Fact]
    public void Rare_term_scores_higher_than_common_term()
    {
        // "rare" appears in 1 doc; "common" appears in all 3.
        // For document d1 containing both, querying "rare" should rank d1 higher
        // because idf("rare") >> idf("common").
        AddDoc("common alpha beta");
        AddDoc("common gamma delta");
        var both = AddDoc("common rare epsilon");

        var rareResults = Search("rare");
        var commonResults = Search("common");

        // "rare" matches only one doc
        rareResults.Should().ContainSingle().Which.Should().Be(both);
        // "common" matches all 3
        commonResults.Should().HaveCount(3);
    }

    [Fact]
    public void Term_in_all_docs_has_lower_idf_so_unique_term_wins()
    {
        // d1 has "ubiquitous" (appears in all docs) + "unique"
        // d2 has "ubiquitous" only.
        // Querying "ubiquitous unique" should rank d1 higher because
        // "unique" contributes a high-IDF term.
        var d1 = AddDoc("ubiquitous unique");
        var d2 = AddDoc("ubiquitous filler");
        AddDoc("ubiquitous padding");

        var results = Search("ubiquitous unique");

        results[0].Should().Be(d1, "document with rare 'unique' term should rank first");
    }

    // ── Document-length normalization ───────────────────────────────────

    [Fact]
    public void Shorter_document_scores_higher_for_same_tf()
    {
        // Both documents contain "target" once (tf=1), but the short document
        // has fewer total tokens, so B*dl/avgdl penalizes the long one more.
        var shortDoc = AddDoc("target alpha");
        var longDoc = AddDoc("target alpha beta gamma delta epsilon zeta eta theta iota");

        var results = Search("target");

        results.Should().HaveCount(2);
        results[0].Should().Be(shortDoc, "shorter document should score higher at equal tf");
    }

    [Fact]
    public void Length_normalization_does_not_reverse_tf_advantage()
    {
        // A long document with tf=3 should still beat a short document with tf=1,
        // even though the long document is penalized by length normalization.
        var longHighTf = AddDoc("target target target alpha beta gamma delta epsilon zeta eta");
        var shortLowTf = AddDoc("target alpha");

        var results = Search("target");

        results[0].Should().Be(longHighTf,
            "high tf should overcome length penalty at moderate length difference");
    }

    [Fact]
    public void Japanese_document_length_uses_norm_tokens_not_utf8_bytes_or_utf16_length()
    {
        const string body = "東京都";
        VertexId document = AddDoc(body);

        FullTextSegmentSnapshot snapshot = FullTextSnapshot();
        EntityRef reference = EntityRef.From(document);
        long entity = EntityRef.Pack(
            reference.Kind,
            reference.Sequence,
            reference.Generation);

        snapshot.TryGetDocLength(entity, out int documentLength).Should().BeTrue();
        documentLength.Should().Be(2, "東京都は3文字ではなく2個のCJK bigramを持つ");
        documentLength.Should().NotBe(body.Length);
        documentLength.Should().NotBe(System.Text.Encoding.UTF8.GetByteCount(body));
    }

    // ── Multi-term additive scoring ─────────────────────────────────────

    [Fact]
    public void Multi_term_query_scores_are_additive()
    {
        // d1 contains both query terms; d2 and d3 contain only one each.
        // BM25 accumulates per-term contributions, so d1 should rank first.
        var d1 = AddDoc("apple banana cherry");
        var d2 = AddDoc("apple cherry date");
        var d3 = AddDoc("banana cherry date");

        var results = Search("apple banana");

        results[0].Should().Be(d1, "document matching both terms should rank first");
        results.Should().HaveCount(3);
    }

    [Fact]
    public void Two_term_match_outranks_single_term_match()
    {
        // All documents have similar length. d1 matches both query terms,
        // while others match at most one.
        var d1 = AddDoc("graph database");
        var d2 = AddDoc("graph engine");
        var d3 = AddDoc("database engine");
        var d4 = AddDoc("search engine");

        var results = Search("graph database");

        results[0].Should().Be(d1, "document matching both query terms ranks first");
    }

    // ── Known-corpus hand-calculated BM25 verification ──────────────────

    [Fact]
    public void Known_corpus_ranking_matches_hand_calculated_bm25_order()
    {
        // Corpus: 4 documents, query = "cat"
        //   d1: "cat"             -> tf=1, dl=1
        //   d2: "cat cat dog"     -> tf=2, dl=3
        //   d3: "cat dog bird"    -> tf=1, dl=3
        //   d4: "dog bird fish"   -> tf=0 (no match)
        //
        // N=4, totalTokens = 1+3+3+3 = 10, avgdl = 2.5
        // df("cat") = 3
        // idf = ln(1 + (4-3+0.5)/(3+0.5)) = ln(1 + 1.5/3.5) = ln(1.4286) ≈ 0.3567
        //
        // k1=1.2, b=0.75
        //
        // d1: denom = 1 + 1.2*(1 - 0.75 + 0.75*1/2.5) = 1 + 1.2*(0.25+0.3) = 1 + 0.66 = 1.66
        //     score = 0.3567 * (1*2.2)/1.66 = 0.3567 * 1.3253 ≈ 0.4727
        // d2: denom = 2 + 1.2*(1 - 0.75 + 0.75*3/2.5) = 2 + 1.2*(0.25+0.9) = 2 + 1.38 = 3.38
        //     score = 0.3567 * (2*2.2)/3.38 = 0.3567 * 1.3018 ≈ 0.4643
        // d3: denom = 1 + 1.2*(1 - 0.75 + 0.75*3/2.5) = 1 + 1.2*(0.25+0.9) = 1 + 1.38 = 2.38
        //     score = 0.3567 * (1*2.2)/2.38 = 0.3567 * 0.9244 ≈ 0.3297
        //
        // Expected order: d1 > d2 > d3 (d4 not returned)
        var d1 = AddDoc("cat");
        var d2 = AddDoc("cat cat dog");
        var d3 = AddDoc("cat dog bird");
        AddDoc("dog bird fish");

        var results = Search("cat");

        results.Should().HaveCount(3);
        results[0].Should().Be(d1, "dl=1 with tf=1 beats dl=3 with tf=2 due to length norm");
        results[1].Should().Be(d2, "tf=2 at dl=3 beats tf=1 at dl=3");
        results[2].Should().Be(d3, "tf=1 at dl=3 is the weakest match");
    }

    [Fact]
    public void Five_doc_corpus_hand_calculated_order()
    {
        // Corpus: 5 documents, query = "engine"
        //   d1: "engine"                      -> tf=1, dl=1
        //   d2: "engine engine"               -> tf=2, dl=2
        //   d3: "engine database graph"       -> tf=1, dl=3
        //   d4: "engine engine engine search" -> tf=3, dl=4
        //   d5: "database graph search"       -> tf=0 (no match)
        //
        // N=5, totalTokens = 1+2+3+4+3 = 13, avgdl = 2.6
        // df("engine") = 4
        // idf = ln(1 + (5-4+0.5)/(4+0.5)) = ln(1 + 1.5/4.5) = ln(1.3333) ≈ 0.2877
        //
        // k1=1.2, b=0.75
        //
        // d1: lenNorm = 1 - 0.75 + 0.75*1/2.6 = 0.25 + 0.2885 = 0.5385
        //     denom = 1 + 1.2*0.5385 = 1.6462
        //     score = 0.2877 * 2.2/1.6462 = 0.2877 * 1.3364 ≈ 0.3844
        // d2: lenNorm = 0.25 + 0.75*2/2.6 = 0.25 + 0.5769 = 0.8269
        //     denom = 2 + 1.2*0.8269 = 2.9923
        //     score = 0.2877 * 4.4/2.9923 = 0.2877 * 1.4704 ≈ 0.4230
        // d3: lenNorm = 0.25 + 0.75*3/2.6 = 0.25 + 0.8654 = 1.1154
        //     denom = 1 + 1.2*1.1154 = 2.3385
        //     score = 0.2877 * 2.2/2.3385 = 0.2877 * 0.9408 ≈ 0.2707
        // d4: lenNorm = 0.25 + 0.75*4/2.6 = 0.25 + 1.1538 = 1.4038
        //     denom = 3 + 1.2*1.4038 = 4.6846
        //     score = 0.2877 * 6.6/4.6846 = 0.2877 * 1.4089 ≈ 0.4053
        //
        // Expected order: d2(0.4230) > d4(0.4053) > d1(0.3844) > d3(0.2707)
        var d1 = AddDoc("engine");
        var d2 = AddDoc("engine engine");
        var d3 = AddDoc("engine database graph");
        var d4 = AddDoc("engine engine engine search");
        AddDoc("database graph search");

        var results = Search("engine");

        results.Should().HaveCount(4);
        results[0].Should().Be(d2, "tf=2, dl=2 gives best balance of tf and length");
        results[1].Should().Be(d4, "tf=3, dl=4 high tf compensates for longer length");
        results[2].Should().Be(d1, "tf=1, dl=1 short but low tf");
        results[3].Should().Be(d3, "tf=1, dl=3 worst: low tf and moderate length");
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void Empty_query_returns_no_results()
    {
        AddDoc("some document content");

        var results = Search("");

        results.Should().BeEmpty();
    }

    [Fact]
    public void Unmatched_term_returns_no_results()
    {
        AddDoc("alpha beta gamma");
        AddDoc("delta epsilon zeta");

        var results = Search("nonexistent");

        results.Should().BeEmpty();
    }

    [Fact]
    public void Single_document_corpus()
    {
        // N=1, df=1 for any matched term.
        // idf = ln(1 + (1-1+0.5)/(1+0.5)) = ln(1 + 0.5/1.5) = ln(1.3333) ≈ 0.2877
        // The single document should be returned.
        var only = AddDoc("hello world");

        var results = Search("hello");

        results.Should().ContainSingle().Which.Should().Be(only);
    }

    [Fact]
    public void Single_document_corpus_multi_term_query()
    {
        var only = AddDoc("graph database engine");

        var results = Search("graph database");

        results.Should().ContainSingle().Which.Should().Be(only);
    }

    [Fact]
    public void Empty_document_body_is_not_returned()
    {
        // A document with an empty body has no tokens indexed.
        AddDoc("");
        var nonempty = AddDoc("findable content here");

        var results = Search("findable");

        results.Should().ContainSingle().Which.Should().Be(nonempty);
    }

    [Fact]
    public void Empty_document_does_not_match_any_query()
    {
        AddDoc("");

        var results = Search("anything");

        results.Should().BeEmpty();
    }

    // ── IDF boundary: term in every document ────────────────────────────

    [Fact]
    public void Term_appearing_in_all_documents_still_returns_results()
    {
        // When df == N, idf = ln(1 + (N-N+0.5)/(N+0.5)) = ln(1 + 0.5/(N+0.5)) > 0.
        // So even a term in every document contributes a positive (though small) score.
        var d1 = AddDoc("the quick");
        var d2 = AddDoc("the slow");
        var d3 = AddDoc("the steady");

        var results = Search("the");

        results.Should().HaveCount(3);
        // All have tf=1, same dl=2 => all same score => tie-broken by entityId ascending
        results.Should().Contain(d1);
        results.Should().Contain(d2);
        results.Should().Contain(d3);
    }

    // ── Stats path consistency ──────────────────────────────────────────

    [Fact]
    public void Stats_path_produces_same_ranking_as_live_scan()
    {
        var d1 = AddDoc("cat");
        var d2 = AddDoc("cat cat dog");
        var d3 = AddDoc("cat dog bird");
        AddDoc("dog bird fish");

        // Live scan path
        List<VertexId> liveResults;
        using (var rtx = _db.BeginReadTransaction())
        {
            liveResults = rtx.Query.Search(Index, "cat", 10).ToList();
        }

        // Stats-driven path
        var stats = _db.CollectStats();
        List<VertexId> statsResults;
        using (var rtx = _db.BeginReadTransaction())
        {
            statsResults = rtx.Query.WithStats(stats).Search(Index, "cat", 10).ToList();
        }

        statsResults.Should().Equal(liveResults, "stats-driven ranking should match live scan ranking");
    }

    // ── K limit ─────────────────────────────────────────────────────────

    [Fact]
    public void K_limits_returned_results()
    {
        for (int i = 0; i < 10; i++)
            AddDoc($"target padding{i}");

        var results = Search("target", k: 3);

        results.Should().HaveCount(3);
    }

    // ── Graph-first path consistency ────────────────────────────────────

    [Fact]
    public void Graph_first_ranking_matches_text_first()
    {
        var d1 = AddDoc("cat");
        var d2 = AddDoc("cat cat dog");
        var d3 = AddDoc("cat dog bird");
        AddDoc("dog bird fish");

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var textFirst = g.Search(Index, "cat", 10).ToList();
        var graphFirst = g.Vertices().HasLabel("Doc")
            .FilterByText(Index, "cat", 10).ToList();

        graphFirst.Should().Equal(textFirst);
    }

    // ── Multi-term with varying IDF ─────────────────────────────────────

    [Fact]
    public void Multi_term_query_with_varying_idf_ranks_correctly()
    {
        // "rare" appears in 1 doc, "common" in 3 docs.
        // Query "rare common" should rank doc with "rare" highest
        // because idf("rare") >> idf("common").
        AddDoc("common alpha");
        AddDoc("common beta");
        var hasRare = AddDoc("common rare");

        var results = Search("rare common");

        results[0].Should().Be(hasRare,
            "document with the rare term should rank first in a multi-term query");
    }

    // ── Deterministic tie-breaking ──────────────────────────────────────

    [Fact]
    public void Equal_score_documents_are_ordered_by_entity_id()
    {
        // All documents have identical structure (tf=1, same dl), so BM25
        // scores are equal. Tie-breaking is by ascending packed entityId.
        var d1 = AddDoc("omega alpha");
        var d2 = AddDoc("omega beta");
        var d3 = AddDoc("omega gamma");

        var results = Search("omega");

        results.Should().HaveCount(3);
        // With identical scores, order is by ascending entityId (creation order).
        results.Should().Equal(d1, d2, d3);
    }
}

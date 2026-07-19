using System.Linq;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 文書単位で枝刈りする WAND を検証する。
/// text-first 演算子は GraphStats のスナップショットがある場合だけ WAND を使い、
/// 統計が無い場合は語単位の全走査を使う。
/// 静的コーパスで WAND と全走査の上位 k 件が完全に一致すること、
/// まれな語と一般的な語の組み合わせで枝刈りが働くこと、
/// 削除済み文書が見えないことを確認する。
/// </summary>
public sealed class FullTextWandTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public FullTextWandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts8_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));
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

    /// <summary>統計を渡さず、語単位の全走査へフォールバックする。</summary>
    private List<VertexId> SearchFullScan(string query, int k)
    {
        using var rtx = _db.BeginReadTransaction();
        return rtx.Query.Search(Index, query, k).ToList();
    }

    /// <summary>統計を渡し、語ごとの上限値を使う WAND 経路を選択する。</summary>
    private List<VertexId> SearchWand(string query, int k)
    {
        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadTransaction();
        return rtx.Query.WithStats(stats).Search(Index, query, k).ToList();
    }

    private void AddDocsBatch(IReadOnlyList<string> bodies)
    {
        using var tx = _db.BeginWriteTransaction();
        foreach (var body in bodies)
        {
            var n = tx.CreateVertex("Doc");
            tx.SetProperty(n, "body", PropertyValue.FromString(body));
        }
        tx.Commit();
    }

    private FullTextSegmentSnapshot Ft()
    {
        using var transaction = _db.BeginReadTransaction();
        ITransaction inner = transaction.AsInternal().Inner;
        inner.FullTextSegments.Should().NotBeNull();
        inner.FullTextSegments!.TryOpen(inner, Index, out var ft).Should().BeTrue();
        return ft;
    }

    [Fact]
    public void Wand_matches_full_scan_on_a_varied_corpus()
    {
        // Deterministic, varied corpus: a few high-df "common" terms plus many rare ones,
        // so multi-term queries genuinely exercise WAND pivoting/skipping. Distinct doc
        // lengths + term frequencies make BM25 scores distinct, so the top-k order is
        // unambiguous and WAND must reproduce it exactly.
        var rng = new Random(12345);
        string[] common = { "alpha", "beta", "gamma" };
        for (int i = 0; i < 120; i++)
        {
            var sb = new System.Text.StringBuilder();
            // 1-3 common terms (high df)
            int nc = rng.Next(1, 4);
            for (int c = 0; c < nc; c++) sb.Append(common[rng.Next(common.Length)]).Append(' ');
            // a handful of rare, mostly-unique terms (low df, high idf)
            int nr = rng.Next(2, 8);
            for (int r = 0; r < nr; r++) sb.Append("r").Append(rng.Next(0, 60)).Append(' ');
            // unique filler so doc lengths differ
            for (int f = 0; f <= (i % 5); f++) sb.Append("u").Append(i).Append('_').Append(f).Append(' ');
            AddDoc(sb.ToString());
        }

        string[] queries =
        {
            "alpha", "alpha beta", "beta gamma", "alpha beta gamma",
            "alpha r3", "r7 r12 beta", "r1 r2 r3", "gamma r40 r41",
        };
        foreach (var q in queries)
        {
            var wand = SearchWand(q, k: 10);
            var full = SearchFullScan(q, k: 10);
            wand.Should().Equal(full, "WAND must reproduce the full-scan top-k exactly for query '{0}'", q);
        }
    }

    [Fact]
    public void Wand_finds_rare_term_doc_among_many_common()
    {
        // 200 docs all contain the common term; only one also contains the rare term.
        // WAND must surface that doc first (rare term's high idf) without scoring every
        // posting of the common term — the core pruning win.
        VertexId target = default;
        for (int i = 0; i < 200; i++)
        {
            string body = i == 137 ? "common rareneedle" : "common";
            var id = AddDoc(body);
            if (i == 137) target = id;
        }

        var wand = SearchWand("common rareneedle", k: 5);
        wand.Should().NotBeEmpty();
        wand[0].Should().Be(target);
        wand.Should().Equal(SearchFullScan("common rareneedle", k: 5));
    }

    [Fact]
    public void Wand_truncates_to_k()
    {
        for (int i = 0; i < 30; i++) AddDoc("term filler" + i);
        SearchWand("term", k: 7).Should().HaveCount(7);
    }

    [Fact]
    public void Wand_excludes_deleted_doc()
    {
        var keep = AddDoc("secret keepme");
        var drop = AddDoc("secret dropme");

        using (var tx = _db.BeginWriteTransaction())
        {
            tx.DeleteVertex(drop);
            tx.Commit();
        }

        var hits = SearchWand("secret", k: 10);
        hits.Should().ContainSingle().Which.Should().Be(keep);
    }

    [Fact]
    public void Wand_japanese_bigram_query()
    {
        var tokyo = AddDoc("東京都");   // bigrams 東京, 京都 + unigrams 東, 京, 都
        AddDoc("京都府");               // bigrams 京都, 都府 + unigrams 京, 都, 府

        // "東京" → tokens 東京, 東, 京 → both docs match via unigram "京"; tokyo ranks first.
        var wandResult = SearchWand("東京", k: 10);
        wandResult.Should().HaveCount(2);
        wandResult[0].Should().Be(tokyo);
        SearchWand("京都", k: 10).Should().Equal(SearchFullScan("京都", k: 10));
    }

    [Fact]
    public void Wand_empty_query_returns_nothing()
    {
        AddDoc("anything at all");
        SearchWand("   ", k: 10).Should().BeEmpty();
    }

    [Fact]
    public void Wand_matches_full_scan_across_multiple_btree_leaves()
    {
        // ~900 docs share "common" so its postings span several B+Tree leaves (≈313
        // entries per 8160-byte leaf). WAND must traverse leaf links and, while pruning
        // the low-idf common-only docs, SeekTo across leaf boundaries via the root descent
        // (BTreeRawCursor's slow path — the skip-pointer substitute, untested by the small
        // corpora). A rare "needle" seeded into scattered docs supplies the pivots that
        // force those cross-leaf seeks. Two-term queries keep the comparison free of
        // float summation-order differences (those need 3+ terms; review item #2).
        const int n = 900;
        var needleAt = new HashSet<int> { 50, 200, 400, 480, 620, 770, 899 };
        var bodies = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            var sb = new System.Text.StringBuilder("common");
            for (int f = 0; f <= i % 6; f++) sb.Append(" f").Append(i).Append('_').Append(f);
            if (needleAt.Contains(i)) sb.Append(" needle");
            bodies.Add(sb.ToString());
        }
        AddDocsBatch(bodies);

        foreach (var q in new[] { "common", "common needle", "needle" })
        {
            var wand = SearchWand(q, k: 10);
            var full = SearchFullScan(q, k: 10);
            wand.Should().Equal(full, "WAND must equal the full scan across multiple leaves for '{0}'", q);
        }
    }

    [Fact]
    public void Wand_does_not_drop_docs_when_snapshot_maxTf_is_stale()
    {
        // 監査 #3 (WAND staleness, spec: 07_fulltext.md#wand): WAND の per-term 上限が snapshot の
        // maxTf/minDocLen に依存していると、snapshot 後に高 tf 文書が増えたとき上限が過小評価され、
        // WAND が「どの残り文書も theta を超えられない」と誤判定して高スコア文書を取りこぼす
        // (exact top-k 違反)。漸近上限 idf*(K1+1) は tf/docLen に依らない真の上限なので解消する。
        AddDoc("wandterm");                       // seed: tf=1 → snapshot maxTf=1, minDocLen=1
        var stale = _db.CollectStats();           // この時点の (df,maxTf,minDocLen,n,avgdl) を凍結

        // snapshot 後に高 tf 文書を追加する (WAND が読むのはライブ postings)。
        AddDoc("wandterm wandterm");                                          // tf=2
        AddDoc(string.Join(' ', Enumerable.Repeat("wandterm", 5)));          // tf=5
        AddDoc(string.Join(' ', Enumerable.Repeat("wandterm", 10)));         // tf=10

        var corpus = stale.FullTextCorpus(Index)!.Value;
        var ft = Ft();
        var tokenizer = ft.Tokenizer;

        // 同一 (stale) stats 基準での exact 全走査と WAND を比較する。
        var exact = Bm25Scorer.Rank(
            ft, tokenizer, "wandterm", corpus.DocumentCount, corpus.AverageDocLength,
            candidateSequences: null, termStats: corpus.Terms).Take(2).ToList();
        var wand = Bm25Scorer.RankWand(
            ft, tokenizer, "wandterm", corpus.DocumentCount, corpus.AverageDocLength, corpus.Terms!, k: 2)!;

        wand.Should().Equal(exact,
            "WAND must return the same exact top-k as the full scan under the same stats basis " +
            "even when the snapshot maxTf/minDocLen are stale relative to the live postings (audit #3)");
    }

    [Fact]
    public void Wand_liveness_filter_drops_dead_doc_and_fills_from_next_live()
    {
        // Directly exercise RankWand's inline liveness filter (the isLive==false branch):
        // DeleteVertex physically removes postings, so a black-box delete never produces the
        // dead/slot-reused orphan posting the filter guards against. Here we feed an
        // explicit isLive that excludes the top-scoring (genuinely live) doc, simulating
        // that orphan, and assert the k-bounded heap still returns k live docs — i.e. the
        // next-best live doc fills in rather than the result shrinking below k.
        var ids = new List<VertexId>();
        for (int i = 1; i <= 6; i++)
            ids.Add(AddDoc(string.Join(' ', Enumerable.Repeat("x", i)))); // tf = docLen = i → distinct, monotone scores

        var corpus = _db.CollectStats().FullTextCorpus(Index)!.Value;
        var ft = Ft();
        var tokenizer = ft.Tokenizer;

        var full = Bm25Scorer.RankWand(
            ft, tokenizer, "x", corpus.DocumentCount, corpus.AverageDocLength, corpus.Terms!, k: 6, isLive: null)!;
        full.Should().HaveCount(6);
        long deadPacked = full[0]; // the highest-scoring doc (tf=6)

        var filtered = Bm25Scorer.RankWand(
            ft, tokenizer, "x", corpus.DocumentCount, corpus.AverageDocLength, corpus.Terms!,
            k: 3, isLive: packed => packed != deadPacked)!;

        filtered.Should().HaveCount(3, "the heap is bounded to k, so a filtered doc must be replaced by the next live one");
        filtered.Should().NotContain(deadPacked);
        filtered.Should().Equal(full.Where(p => p != deadPacked).Take(3));
    }
}

using FluentAssertions;
using Xunit;

namespace Quiver.Rag.Tests;

/// <summary>
/// RagSearcher の 3 シナリオ (固有名詞→BM25 / 言い換え→KNN /
/// expansion 連結) + 隣接ヒットのマージ + MetadataFilter を検証する。
/// </summary>
/// <remarks>
/// 各段落が独立した 1 チャンクになるよう TargetSize=40 を使う (段落長 20〜40 char。各段落 ≤40 で
/// 分割されず、隣接段落の対は &gt;40 でパックもされない)。これで検索語が単語境界で割れない。
/// </remarks>
public sealed class RagSearchTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private const int Dim = 8;

    public RagSearchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_rag4_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // FakeEmbedder と同一式で query ベクトルを作るための共有関数。
    private static float[] Embed(string s, int dim)
    {
        var v = new float[dim];
        for (int j = 0; j < s.Length; j++) v[j % dim] += (s[j] % 17) * 0.01f;
        if (v.All(x => x == 0f)) v[0] = 1f;
        return v;
    }

    private sealed class FakeEmbedder : IChunkEmbedder
    {
        public int Dimensions => Dim;
        public ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var result = new float[texts.Count][];
            for (int i = 0; i < texts.Count; i++) result[i] = Embed(texts[i], Dim);
            return ValueTask.FromResult(result);
        }
    }

    private RagStore NewStore(QuiverDatabase db) =>
        new(db, new RagStoreOptions
        {
            EmbeddingDimensions = Dim,
            Chunking = new ChunkingOptions { TargetSize = 40, Overlap = 0 },
        });

    private static IngestedDocument Doc(string id, IReadOnlyDictionary<string, string>? meta, params string[] paras) =>
        new(id, "title-" + id, meta ?? new Dictionary<string, string>(),
            paras.Select(p => new IngestedBlock(BlockKind.Paragraph, p)).ToArray());

    [Fact]
    public async Task Bm25_finds_chunk_with_rare_proper_noun()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(
            Doc("d1", null,
                "the calm cat sat on the mat",
                "we meet Zphobos in the glade",
                "then we all walked back home"),
            new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Zphobos", queryVector: null, new RagSearchOptions { NeighborExpansion = 0 });

        hits.Should().NotBeEmpty();
        hits[0].ChunkText.Should().Contain("Zphobos");
        hits[0].Document.SourceId.Should().Be("d1");
        hits[0].Score.Bm25Score.Should().BeGreaterThan(0);
        hits[0].Score.VectorSimilarity.Should().BeNull();
        hits[0].Score.FusedScore.Should().Be(hits[0].Score.Bm25Score!.Value);
        hits[0].Score.FusionMethod.Should().Be(RagFusionMethod.TextOnly);
        hits[0].Score.ReciprocalRankConstant.Should().Be(0);
    }

    [Fact]
    public async Task Bm25_finds_chunk_by_heading_word_absent_from_body()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        // 固有語 "Quetzal" は見出しにだけ置き、本文には含めない。
        var doc = new IngestedDocument("d1", "title-d1", new Dictionary<string, string>(),
            new[]
            {
                new IngestedBlock(BlockKind.Heading, "Quetzal", HeadingLevel: 1),
                new IngestedBlock(BlockKind.Paragraph, "the body mentions only cats and dogs here"),
            });
        await store.UpsertDocumentAsync(doc, new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Quetzal", null, new RagSearchOptions { NeighborExpansion = 0 });

        hits.Should().NotBeEmpty();
        // 本文 (text) に "Quetzal" は無いが headingPath 経由で searchText に載り BM25 が引き当てる。
        hits[0].ChunkText.Should().NotContain("Quetzal");
        hits[0].HeadingPath.Should().Contain("Quetzal");
    }

    [Fact]
    public async Task Knn_finds_chunk_nearest_to_query_vector()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(
            Doc("d1", null,
                "alpha apple alpha apple chunk",
                "bravo banana bravo orange set",
                "charlie cherry plum melon kiwi"),
            new FakeEmbedder());

        // 見出し無しなので埋め込み入力 = 本文。対象チャンクと同じベクトルを query に使う。
        var q = Embed("bravo banana bravo orange set", Dim);

        var searcher = new RagSearcher(store);
        var hits = searcher.Search(queryText: "", q, new RagSearchOptions { NeighborExpansion = 0 });

        hits.Should().NotBeEmpty();
        hits[0].ChunkText.Should().Contain("bravo banana");
        hits[0].Score.Bm25Score.Should().BeNull();
        hits[0].Score.VectorSimilarity.Should().NotBeNull();
        hits[0].Score.FusedScore.Should()
            .BeApproximately(hits[0].Score.VectorSimilarity!.Value, 1e-6);
        hits[0].Score.FusionMethod.Should().Be(RagFusionMethod.VectorOnly);
    }

    [Fact]
    public async Task Expansion_concatenates_neighbor_chunks()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(
            Doc("d1", null,
                "the first part opens the scene",
                "the middle Zphobos part is key",
                "the third part closes it nicely"),
            new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Zphobos", null, new RagSearchOptions { NeighborExpansion = 1 });

        hits.Should().HaveCount(1);
        hits[0].ChunkText.Should().Contain("first part");
        hits[0].ChunkText.Should().Contain("Zphobos");
        hits[0].ChunkText.Should().Contain("third part");
    }

    [Fact]
    public async Task Adjacent_hits_merge_into_single_result()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        // "common" が隣接 2 チャンクに出る → 2 ヒットだが 1 つの RagHit にマージ。
        await store.UpsertDocumentAsync(
            Doc("d1", null,
                "common alpha beta gamma delta",
                "common bravo zeta eta thetaxx",
                "zzz totally unrelated words now"),
            new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("common", null, new RagSearchOptions { NeighborExpansion = 1, K = 10 });

        hits.Should().HaveCount(1); // 隣接ヒットはマージされ重複文脈を返さない
        hits[0].ChunkText.Should().Contain("common alpha");
        hits[0].ChunkText.Should().Contain("common bravo");
        hits[0].Score.Bm25Score.Should().BeGreaterThan(0,
            "隣接チャンクのマージ後も代表ヒットの score 内訳を保持する");
    }

    [Fact]
    public async Task Neighbor_expansion_zero_returns_only_center()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(
            Doc("d1", null,
                "aaa first line of the texts",
                "bbb Zphobos middle line texts",
                "ccc last line of the textaaa"),
            new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Zphobos", null, new RagSearchOptions { NeighborExpansion = 0 });

        hits.Should().HaveCount(1);
        hits[0].ChunkText.Should().Be("bbb Zphobos middle line texts");
        hits[0].ChunkText.Should().NotContain("first").And.NotContain("last");
    }

    [Fact]
    public async Task Metadata_filter_excludes_documents()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var searcher = new RagSearcher(store);

        await store.UpsertDocumentAsync(
            Doc("pub", new Dictionary<string, string> { ["acl"] = "public" }, "shared Zphobos public note text"),
            new FakeEmbedder());
        await store.UpsertDocumentAsync(
            Doc("sec", new Dictionary<string, string> { ["acl"] = "secret" }, "secret Zphobos hidden note text"),
            new FakeEmbedder());

        var hits = searcher.Search("Zphobos", null, new RagSearchOptions
        {
            NeighborExpansion = 0,
            MetadataFilter = m => m.Metadata.TryGetValue("acl", out var acl) && acl == "public",
        });

        hits.Should().HaveCount(1);
        hits[0].Document.SourceId.Should().Be("pub");
    }

    [Fact]
    public async Task Metadata_equals_pushdown_excludes_non_matching_documents()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var searcher = new RagSearcher(store);

        await store.UpsertDocumentAsync(
            Doc("pub", new Dictionary<string, string> { ["acl"] = "public" }, "shared Zphobos public note text"),
            new FakeEmbedder());
        await store.UpsertDocumentAsync(
            Doc("sec", new Dictionary<string, string> { ["acl"] = "secret" }, "secret Zphobos hidden note text"),
            new FakeEmbedder());

        var hits = searcher.Search("Zphobos", null, new RagSearchOptions
        {
            NeighborExpansion = 0,
            MetadataEquals = new Dictionary<string, string> { ["acl"] = "public" },
        });

        hits.Should().HaveCount(1);
        hits[0].Document.SourceId.Should().Be("pub");
    }

    [Fact]
    public async Task Metadata_equals_pushdown_avoids_recall_hole()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var searcher = new RagSearcher(store);

        // "Zphobos" を 8 件の secret 文書に、1 件だけ public 文書に入れる。
        for (int i = 0; i < 8; i++)
            await store.UpsertDocumentAsync(
                Doc($"sec{i}", new Dictionary<string, string> { ["acl"] = "secret" },
                    $"secret Zphobos document number {i} body"),
                new FakeEmbedder());
        await store.UpsertDocumentAsync(
            Doc("pub", new Dictionary<string, string> { ["acl"] = "public" }, "public Zphobos rare allowed note"),
            new FakeEmbedder());

        // K=1。後段フィルタなら top-1 が secret になり public が落ちる (recall hole)。
        // push-down なら public のチャンクだけが母集団なので必ず見つかる。
        var hits = searcher.Search("Zphobos", null, new RagSearchOptions
        {
            K = 1,
            NeighborExpansion = 0,
            MetadataEquals = new Dictionary<string, string> { ["acl"] = "public" },
        });

        hits.Should().HaveCount(1);
        hits[0].Document.SourceId.Should().Be("pub");
    }

    [Fact]
    public async Task Metadata_equals_vector_pushdown_returns_candidate_top_one_before_global_neighbors()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var searcher = new RagSearcher(store);
        const string globalNearest = "secret vector nearest phrase body";

        for (int i = 0; i < 8; i++)
            await store.UpsertDocumentAsync(
                Doc(
                    $"sec{i}",
                    new Dictionary<string, string> { ["acl"] = "secret" },
                    globalNearest),
                new FakeEmbedder());
        await store.UpsertDocumentAsync(
            Doc(
                "pub",
                new Dictionary<string, string> { ["acl"] = "public" },
                "public allowed vector candidate body"),
            new FakeEmbedder());

        IReadOnlyList<RagHit> hits = searcher.Search(
            queryText: "",
            Embed(globalNearest, Dim),
            new RagSearchOptions
            {
                K = 1,
                NeighborExpansion = 0,
                MetadataEquals =
                    new Dictionary<string, string> { ["acl"] = "public" },
            });

        hits.Should().ContainSingle();
        hits[0].Document.SourceId.Should().Be("pub");
        hits[0].Score.VectorSimilarity.Should().NotBeNull();
        hits[0].Score.FusionMethod.Should().Be(RagFusionMethod.VectorOnly);
    }

    [Fact]
    public async Task Metadata_equals_pushdown_returns_empty_when_no_document_matches()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(
            Doc("d1", new Dictionary<string, string> { ["acl"] = "secret" }, "secret Zphobos note text body"),
            new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Zphobos", null, new RagSearchOptions
        {
            MetadataEquals = new Dictionary<string, string> { ["acl"] = "public" }, // 一致文書なし
        });

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Metadata_equals_pushdown_works_for_hybrid()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(
            Doc("pub", new Dictionary<string, string> { ["acl"] = "public" },
                "alpha Zphobos first block textx",
                "bravo banana second block texts"),
            new FakeEmbedder());
        await store.UpsertDocumentAsync(
            Doc("sec", new Dictionary<string, string> { ["acl"] = "secret" },
                "secret Zphobos first block textx",
                "bravo banana hidden block texts"),
            new FakeEmbedder());

        var q = Embed("bravo banana second block texts", Dim);
        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Zphobos", q, new RagSearchOptions
        {
            NeighborExpansion = 0,
            K = 10,
            MetadataEquals = new Dictionary<string, string> { ["acl"] = "public" },
        });

        hits.Should().NotBeEmpty();
        hits.Should().OnlyContain(h => h.Document.SourceId == "pub"); // secret は母集団から除外
        hits.Select(h => h.ChunkText).Should().Contain(t => t.Contains("Zphobos"));
        hits.Should().OnlyContain(hit =>
            hit.Score.FusionMethod == RagFusionMethod.ReciprocalRankFusion
            && hit.Score.ReciprocalRankConstant == 60
            && hit.Score.FusedScore > 0);
    }

    [Fact]
    public async Task Hybrid_search_returns_results_from_both_signals()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(
            Doc("d1", null,
                "alpha Zphobos first block textx",
                "bravo banana second block texts",
                "charlie cherry third block textx"),
            new FakeEmbedder());

        var q = Embed("bravo banana second block texts", Dim);
        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Zphobos", q, new RagSearchOptions { NeighborExpansion = 0, K = 10 });

        // BM25 が拾う "Zphobos" チャンクと KNN が拾う "bravo banana" チャンクの双方が融合結果に現れる。
        var texts = hits.Select(h => h.ChunkText).ToList();
        texts.Should().Contain(t => t.Contains("Zphobos"));
        texts.Should().Contain(t => t.Contains("bravo banana"));
        hits.Should().OnlyContain(hit =>
            hit.Score.FusionMethod == RagFusionMethod.ReciprocalRankFusion
            && hit.Score.ReciprocalRankConstant == 60
            && hit.Score.FusedScore > 0);
        hits.Should().Contain(hit =>
            hit.Score.Bm25Score.HasValue
            && hit.Score.VectorSimilarity.HasValue,
            "同じチャンクが両チャンネルに入った場合は両方の生 score を返す");
    }

    [Fact]
    public async Task Expansion_reconstructs_split_paragraph_without_spurious_separator()
    {
        // ブロック内分割 (Overlap=0) されたチャンクを expansion で再連結したとき、
        // 偽の区切り ("\n\n") を挟まず原文を完全復元する (ConcatChunks の連続判定を pin)。
        using var db = QuiverDatabase.Open(_path);
        var store = new RagStore(db, new RagStoreOptions
        {
            EmbeddingDimensions = Dim,
            Chunking = new ChunkingOptions { TargetSize = 40, Overlap = 0 },
        });
        // 単語 UNIQ を先頭に置き 1 段落 (>40 char) を分割させる。
        string para = "UNIQ " + string.Join(" ", Enumerable.Repeat("abcdefghij", 5)); // 5 + 54 = 59 char
        await store.UpsertDocumentAsync(Doc("d1", null, para), new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("UNIQ", null, new RagSearchOptions { NeighborExpansion = 10 });

        hits.Should().HaveCount(1);
        hits[0].ChunkText.Should().Be(para);           // 区切り偽挿入なしで原文一致
        hits[0].ChunkText.Should().NotContain("\n\n");
    }

    [Fact]
    public void Empty_query_returns_empty()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var searcher = new RagSearcher(store);

        searcher.Search("", null).Should().BeEmpty();
    }

    [Fact]
    public async Task IncludeDocument_false_omits_document_ref()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(Doc("d1", null, "solo Zphobos only chunk of text"), new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Zphobos", null, new RagSearchOptions { IncludeDocument = false, NeighborExpansion = 0 });

        hits.Should().HaveCount(1);
        hits[0].Document.SourceId.Should().BeEmpty();
        hits[0].ChunkVertexId.Value.Should().NotBe(0); // 代表Vertexは入る
    }

    [Fact]
    public async Task Ranks_are_one_based_and_sorted()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        // 別文書にして expansion でマージされないようにする。
        await store.UpsertDocumentAsync(Doc("d1", null, "Zphobos appears once in textaa"), new FakeEmbedder());
        await store.UpsertDocumentAsync(Doc("d2", null, "Zphobos appears twice Zphobosx"), new FakeEmbedder());
        await store.UpsertDocumentAsync(Doc("d3", null, "Zphobos thrice Zphobos Zphobos"), new FakeEmbedder());

        var searcher = new RagSearcher(store);
        var hits = searcher.Search("Zphobos", null, new RagSearchOptions { NeighborExpansion = 0, K = 10 });

        hits.Should().HaveCountGreaterThan(1);
        hits[0].Rank.Should().Be(1);
        hits.Select(h => h.Rank).Should().BeInAscendingOrder();
    }
}

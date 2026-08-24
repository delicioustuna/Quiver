// Yatagarasu.Samples.Rag — Yatagarasu.Rag による文書取込 → ハイブリッド検索 → graph expansion を
// end-to-end で走らせる。埋め込みはダミー実装。
//
// 実行: dotnet run --project samples/Yatagarasu.Samples.Rag

using Yatagarasu;
using Yatagarasu.Rag;

string dir = Path.Combine(Path.GetTempPath(), "yatagarasu_rag_sample_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = YatagarasuDatabase.Open(Path.Combine(dir, "graph.yata"));

    var embedder = new HashEmbedder(dim: 16);
    var store = new RagStore(db, new RagStoreOptions
    {
        EmbeddingDimensions = embedder.Dimensions,
        IngestionProfile = new RagIngestionProfile
        {
            EmbeddingProfileId = embedder.ProfileId,
        },
        // 各段落を独立チャンクにして expansion を見せるため小さめの目標サイズ。
        Chunking = new ChunkingOptions { TargetSize = 40, Overlap = 0 },
    });

    // ── 合成文書を取込 ──
    var docs = new[]
    {
        new IngestedDocument("guide/yatagarasu", "Yatagarasu 入門",
            new Dictionary<string, string> { ["category"] = "guide" },
            new[]
            {
                new IngestedBlock(BlockKind.Heading, "概要", HeadingLevel: 1),
                new IngestedBlock(BlockKind.Paragraph, "Yatagarasu はグラフとベクトルと全文検索の組込エンジン"),
                new IngestedBlock(BlockKind.Paragraph, "識別子 Zphobos を全文検索で後から引き当てる例"),
                new IngestedBlock(BlockKind.Paragraph, "ヒット地点の前後チャンクを連結し文脈を復元する"),
            }),
        new IngestedDocument("guide/rag", "RAG バックエンド",
            new Dictionary<string, string> { ["category"] = "rag" },
            new[]
            {
                new IngestedBlock(BlockKind.Heading, "なぜグラフか", HeadingLevel: 1),
                new IngestedBlock(BlockKind.Paragraph, "純ベクトル DB はヒット単体しか返せない弱点"),
                new IngestedBlock(BlockKind.Paragraph, "Yatagarasu は隣接チャンクを辿り周辺文脈を返せる"),
            }),
    };

    foreach (var doc in docs)
    {
        var r = await store.UpsertDocumentAsync(doc, embedder);
        Console.WriteLine(
            $"取込 {doc.SourceId}: document={r.DocumentVertexId} chunks={r.ChunkCount} unchanged={r.Unchanged}");
    }

    // profile・本文・属性・revision が同じ再取込は ingestion fingerprint 一致で no-op。
    var again = await store.UpsertDocumentAsync(docs[0], embedder);
    Console.WriteLine(
        $"再取込(同一) {docs[0].SourceId}: document={again.DocumentVertexId} unchanged={again.Unchanged}");
    Console.WriteLine();

    var searcher = new RagSearcher(store);

    // ── 1. 全文検索 (固有名詞 → BM25) + 前後 1 チャンク連結 ──
    Console.WriteLine("── 1. BM25 \"Zphobos\" (expansion=1) ──");
    Print(searcher.Search("Zphobos", queryVector: null, new RagSearchOptions { NeighborExpansion = 1 }));

    // ── 2. ベクトル検索 (言い換えクエリ → KNN) ──
    Console.WriteLine();
    Console.WriteLine("── 2. KNN (クエリ「周辺の文脈」のベクトル) ──");
    float[] qv = (await embedder.EmbedAsync(new[] { "周辺の文脈を返す" }))[0];
    Print(searcher.Search(queryText: "", qv, new RagSearchOptions { NeighborExpansion = 0 }));

    // ── 3. ハイブリッド + メタデータフィルタ ──
    Console.WriteLine();
    Console.WriteLine("── 3. Hybrid + MetadataFilter(category == guide) ──");
    Print(searcher.Search("文脈", qv, new RagSearchOptions
    {
        NeighborExpansion = 1,
        MetadataFilter = m => m.Metadata.TryGetValue("category", out var c) && c == "guide",
    }));

    // ── 文書削除 (チャンク・関係・ベクトルごと) ──
    Console.WriteLine();
    Console.WriteLine($"削除 guide/rag → {store.DeleteDocument("guide/rag")}");

    static void Print(IReadOnlyList<RagHit> hits)
    {
        if (hits.Count == 0) { Console.WriteLine("  (ヒットなし)"); return; }
        foreach (var h in hits)
        {
            string head = string.IsNullOrEmpty(h.HeadingPath) ? "" : $" [{h.HeadingPath}]";
            Console.WriteLine($"  #{h.Rank} 〈{h.Document.Title}〉{head}");
            Console.WriteLine(
                $"      score={h.Score.FusedScore:F6} method={h.Score.FusionMethod}" +
                $" bm25={FormatDouble(h.Score.Bm25Score)} vector={FormatFloat(h.Score.VectorSimilarity)}" +
                $" rrfK={h.Score.ReciprocalRankConstant}");
            Console.WriteLine($"      {h.ChunkText.Replace("\n\n", " / ").Replace("\n", " ")}");
        }

        static string FormatDouble(double? value) => value?.ToString("F6") ?? "-";
        static string FormatFloat(float? value) => value?.ToString("F6") ?? "-";
    }
}
finally
{
    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
}

// 決定的なダミー埋め込み器。文字ヒストグラムを正規化しただけの素朴なベクトル。
// 実運用では使う埋め込みモデル (OpenAI API / ローカル ONNX 等) に対して IChunkEmbedder を直接実装する。
// (詳細は docs/cookbook.md「ローカル RAG」§埋め込み器)。
sealed class HashEmbedder(int dim) : IChunkEmbedder
{
    public string ProfileId => "sample-hash-embedding-v1";
    public int Dimensions { get; } = dim;

    public ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            var v = new float[Dimensions];
            foreach (var ch in texts[i]) v[ch % Dimensions] += 1f;
            float norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0) for (int d = 0; d < Dimensions; d++) v[d] /= norm;
            else v[0] = 1f;
            result[i] = v;
        }
        return ValueTask.FromResult(result);
    }
}

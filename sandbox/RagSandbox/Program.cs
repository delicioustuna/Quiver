using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quiver;
using Quiver.Rag;

// ─── 設定 ──────────────────────────────────────────────────────────────────
const string lmStudioBase = "http://localhost:1234";
const string embeddingModel = "text-embedding-embeddinggemma-300m-qat";
const string chatModel = "google/gemma-4-12b";

string baseDir = Path.Combine(Path.GetTempPath(), "quiver_rag_sandbox_" + Guid.NewGuid().ToString("N")[..8]);

try
{
    await RunRagDemo(baseDir);
}
finally
{
    if (Directory.Exists(baseDir))
        Directory.Delete(baseDir, recursive: true);
}

// ═══════════════════════════════════════════════════════════════════════════
// メイン
// ═══════════════════════════════════════════════════════════════════════════

async Task RunRagDemo(string dir)
{
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine("  Quiver RAG Sandbox");
    Console.WriteLine($"  LM Studio : {lmStudioBase}");
    Console.WriteLine($"  Embedding : {embeddingModel}");
    Console.WriteLine($"  Chat      : {chatModel}");
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine();

    using var http = new HttpClient
    {
        BaseAddress = new Uri(lmStudioBase),
        Timeout = TimeSpan.FromMinutes(5),
    };

    // ── 1. LM Studio 接続確認 ──────────────────────────────────────────
    Console.Write("  LM Studio 接続確認... ");
    try
    {
        var models = await http.GetFromJsonAsync<ModelsResponse>("/v1/models");
        Console.WriteLine($"OK ({models?.Data?.Length ?? 0} モデル)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"失敗: {ex.Message}");
        Console.WriteLine("  → LM Studio でサーバーを起動してください (Status: Stopped → Start)");
        return;
    }

    // ── 2. Embedding 次元数をプローブ ──────────────────────────────────
    Console.Write("  Embedding 次元数を検出... ");
    var embedder = new LmStudioEmbedder(http, embeddingModel);
    int dims;
    try
    {
        dims = await embedder.ProbeAsync();
        Console.WriteLine($"{dims} 次元");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"失敗: {ex.Message}");
        Console.WriteLine($"  → モデル '{embeddingModel}' がロードされているか確認してください");
        return;
    }

    // ── 3. DB + RagStore 作成 ──────────────────────────────────────────
    Console.Write("  DB 作成... ");
    Directory.CreateDirectory(dir);
    using var db = GraphDatabase.Open(Path.Combine(dir, "rag.quiver"));
    var store = new RagStore(db, new RagStoreOptions
    {
        EmbeddingDimensions = dims,
        EnableFullTextIndex = true,
    });
    Console.WriteLine("OK");

    // ── 4. サンプル文書を取込 ──────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("─── 文書取込 ─────────────────────────────────────────");
    var sw = Stopwatch.StartNew();
    var docs = CreateSampleDocuments();
    foreach (var doc in docs)
    {
        var docSw = Stopwatch.StartNew();
        var result = await store.UpsertDocumentAsync(doc, embedder);
        docSw.Stop();
        string status = result.Unchanged ? "SKIP" : " OK ";
        Console.WriteLine($"  [{status}] {doc.Title} → {result.ChunkCount} チャンク ({docSw.ElapsedMilliseconds} ms)");
    }
    sw.Stop();
    Console.WriteLine($"  取込合計: {sw.ElapsedMilliseconds} ms");

    // ── 5. ハイブリッド検索 ────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("─── 検索テスト ───────────────────────────────────────");
    string[] queries =
    [
        "グラフデータベースの利点は何ですか",
        "ベクトル検索とHNSWについて教えて",
        "Quiverのファイル永続化の仕組み",
    ];

    var searcher = new RagSearcher(store);
    foreach (var query in queries)
    {
        Console.WriteLine($"\n  Q: {query}");
        sw.Restart();
        var queryVec = await embedder.EmbedSingleAsync(query);
        var embedMs = sw.ElapsedMilliseconds;
        var hits = searcher.Search(query, queryVec, new RagSearchOptions { K = 3, NeighborExpansion = 0 });
        sw.Stop();
        Console.WriteLine($"    embed: {embedMs} ms / 検索: {sw.ElapsedMilliseconds - embedMs} ms / 合計: {sw.ElapsedMilliseconds} ms / {hits.Count} 件");

        if (hits.Count == 0)
        {
            Console.WriteLine("    (ヒットなし)");
            continue;
        }
        foreach (var hit in hits)
        {
            var preview = hit.ChunkText.ReplaceLineEndings(" ");
            if (preview.Length > 100) preview = preview[..100] + "...";
            Console.WriteLine($"    #{hit.Rank} [{hit.Document.Title}] {preview}");
        }
    }

    // ── 6. RAG 回答生成 ──────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("─── RAG 回答生成 ─────────────────────────────────────");
    string ragQuery = "Quiverの主な特徴と、ローカルRAGバックエンドとしての強みを教えてください。";
    Console.WriteLine($"  Q: {ragQuery}");

    sw.Restart();
    var ragVec = await embedder.EmbedSingleAsync(ragQuery);
    var ragEmbedMs = sw.ElapsedMilliseconds;
    var ragHits = searcher.Search(ragQuery, ragVec, new RagSearchOptions { K = 3, NeighborExpansion = 1 });
    var ragSearchMs = sw.ElapsedMilliseconds;
    Console.WriteLine($"  検索完了: embed {ragEmbedMs} ms + search {ragSearchMs - ragEmbedMs} ms = {ragSearchMs} ms");

    var context = string.Join("\n\n---\n\n",
        ragHits.Select(h => $"[{h.Document.Title}]\n{h.ChunkText}"));

    Console.WriteLine($"  コンテキスト: {ragHits.Count} チャンク, {context.Length} 文字");
    Console.WriteLine();
    Console.Write("  A: ");

    sw.Restart();
    await StreamChatCompletion(http, chatModel, ragQuery, context);
    sw.Stop();
    Console.WriteLine($"  生成完了: {sw.ElapsedMilliseconds} ms");

    Console.WriteLine();
    Console.WriteLine("───────────────────────────────────────────────────────────────");
    Console.WriteLine("  完了。Enterキーで終了します...");
    Console.ReadKey();
}

// ═══════════════════════════════════════════════════════════════════════════
// サンプル文書
// ═══════════════════════════════════════════════════════════════════════════

static IngestedDocument[] CreateSampleDocuments() =>
[
    new(
        SourceId: "doc:quiver-overview",
        Title: "Quiver 概要",
        Metadata: new Dictionary<string, string> { ["category"] = "architecture" },
        Blocks:
        [
            new(BlockKind.Heading, "Quiver とは", HeadingLevel: 1),
            new(BlockKind.Paragraph,
                "Quiver は .NET 向けの組み込み (in-process) グラフデータベースエンジンです。" +
                "サーバープロセスを別途起動する必要がなく、アプリケーションに直接組み込んで使用します。" +
                "SQLite や LiteDB のようなシンプルさを持ちながら、グラフ構造のデータを効率的に扱えます。"),
            new(BlockKind.Heading, "主な機能", HeadingLevel: 2),
            new(BlockKind.Paragraph,
                "Quiver はグラフ検索、ベクトル検索 (HNSW)、全文検索 (BM25) の 3 つの検索方式を統合的に提供します。" +
                "RRF (Reciprocal Rank Fusion) によるハイブリッド検索も利用可能です。" +
                "これにより、ローカル RAG バックエンドとして最適な検索基盤を構築できます。"),
            new(BlockKind.Heading, "永続化", HeadingLevel: 2),
            new(BlockKind.Paragraph,
                "データは単一の .quiver ファイルに永続化されます。" +
                "WAL (Write-Ahead Logging) によりクラッシュ安全性を確保しています。" +
                "運用中は .quiver-wal ファイルが追加で作成されますが、正常終了時にはメインファイルに統合されます。"),
        ]),
    new(
        SourceId: "doc:graph-basics",
        Title: "グラフデータベース入門",
        Metadata: new Dictionary<string, string> { ["category"] = "tutorial" },
        Blocks:
        [
            new(BlockKind.Heading, "グラフデータベースとは", HeadingLevel: 1),
            new(BlockKind.Paragraph,
                "グラフデータベースは、ノード (頂点) とリレーション (辺) でデータの関連性を表現するデータベースです。" +
                "RDB では結合テーブルや JOIN で表現する多対多の関連を、直感的にモデリングできます。"),
            new(BlockKind.Heading, "ノードとリレーション", HeadingLevel: 2),
            new(BlockKind.Paragraph,
                "ノードはエンティティを表し、ラベルと任意のプロパティ (key-value) を持ちます。" +
                "リレーションはノード間の関連を表し、型 (タイプ) とプロパティを持ちます。" +
                "例えば Person ノードが KNOWS リレーションで別の Person に接続する構造が典型的です。"),
            new(BlockKind.Heading, "グラフ走査", HeadingLevel: 2),
            new(BlockKind.Paragraph,
                "グラフデータベースの大きな強みは、関連をたどるトラバーサル操作が高速なことです。" +
                "友達の友達を見つけるような N ホップ探索が、RDB の再帰 JOIN と比べて圧倒的に効率的です。" +
                "Quiver では Gremlin ライクな API や Match DSL でパターンマッチングを行えます。"),
        ]),
    new(
        SourceId: "doc:vector-search",
        Title: "ベクトル検索の仕組み",
        Metadata: new Dictionary<string, string> { ["category"] = "deep-dive" },
        Blocks:
        [
            new(BlockKind.Heading, "ベクトル検索とは", HeadingLevel: 1),
            new(BlockKind.Paragraph,
                "ベクトル検索は、テキストや画像を高次元ベクトル (埋め込み) に変換し、" +
                "ベクトル空間内の近傍を効率的に見つける技術です。" +
                "意味的に類似したコンテンツを発見できるため、RAG (検索拡張生成) の中核技術として使われています。"),
            new(BlockKind.Heading, "HNSW アルゴリズム", HeadingLevel: 2),
            new(BlockKind.Paragraph,
                "Quiver は HNSW (Hierarchical Navigable Small World) グラフを使って近似最近傍探索を実装しています。" +
                "HNSW は多層のスキップリスト構造を持ち、上位層でグローバルに候補を絞り、下位層で精密に探索します。" +
                "高次元空間でも対数的な探索コストで近傍を発見でき、大量のベクトルに対して高速な検索を提供します。"),
            new(BlockKind.Heading, "コサイン類似度", HeadingLevel: 2),
            new(BlockKind.Paragraph,
                "Quiver ではベクトル間の距離計測にコサイン類似度を標準で使用します。" +
                "コサイン類似度はベクトルの方向の一致度を測るため、テキスト埋め込みの比較に適しています。" +
                "値は -1 から 1 の範囲で、1 に近いほど類似度が高いことを示します。"),
        ]),
];

// ═══════════════════════════════════════════════════════════════════════════
// Chat Completion (SSE ストリーミング)
// ═══════════════════════════════════════════════════════════════════════════

async Task StreamChatCompletion(HttpClient http, string chatModelId, string query, string context)
{
    var messages = new object[]
    {
        new
        {
            role = "system",
            content =
                "あなたは質問に答えるアシスタントです。以下のコンテキスト情報のみに基づいて回答してください。\n" +
                "コンテキストに含まれない情報については「コンテキストに該当する情報がありません」と答えてください。\n\n" +
                "コンテキスト:\n" + context,
        },
        new { role = "user", content = query },
    };
    var body = new { model = chatModelId, messages, stream = true, max_tokens = 4096 };

    var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    resp.EnsureSuccessStatusCode();

    using var stream = await resp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);

    int chunks = 0;
    var thinkBuf = new StringBuilder();
    bool inThinking = false;

    while (await reader.ReadLineAsync() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (!line.StartsWith("data: ")) continue;
        var data = line["data: ".Length..];
        if (data == "[DONE]") break;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) continue;
            var choice = choices[0];

            JsonElement container;
            if (choice.TryGetProperty("delta", out container) || choice.TryGetProperty("message", out container))
            {
                if (TryGetText(container, "reasoning_content", out var think))
                {
                    if (!inThinking) { Console.Write("[thinking] "); inThinking = true; }
                    thinkBuf.Append(think);
                }
                if (TryGetText(container, "content", out var text))
                {
                    if (inThinking) { Console.Write($"({thinkBuf.Length} 文字)\n  A: "); inThinking = false; }
                    Console.Write(text);
                    chunks++;
                }
            }
        }
        catch (JsonException) { }
    }

    if (inThinking && chunks == 0)
    {
        Console.WriteLine($"({thinkBuf.Length} 文字)");
        Console.WriteLine($"  [thinking 全文]\n{thinkBuf}");
    }

    if (chunks == 0 && thinkBuf.Length == 0)
    {
        Console.WriteLine("(ストリーミング応答なし — 非ストリーミングで再試行)");
        Console.Write("  A: ");
        var fallback = new { model = chatModelId, messages, stream = false, max_tokens = 4096 };
        var fallbackResp = await http.PostAsync("/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(fallback), Encoding.UTF8, "application/json"));
        fallbackResp.EnsureSuccessStatusCode();
        var fallbackBody = await fallbackResp.Content.ReadAsStringAsync();
        using var fallbackDoc = JsonDocument.Parse(fallbackBody);
        var msg = fallbackDoc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var answer = msg.TryGetProperty("content", out var c) ? c.GetString() : null;
        var reasoning = msg.TryGetProperty("reasoning_content", out var r) ? r.GetString() : null;
        if (!string.IsNullOrEmpty(answer))
            Console.WriteLine(answer);
        else if (!string.IsNullOrEmpty(reasoning))
        {
            Console.WriteLine("(content 空 — reasoning_content を表示)");
            Console.WriteLine(reasoning);
        }
        else
        {
            Console.WriteLine("(応答なし)");
            Console.WriteLine($"  [raw] {fallbackBody[..Math.Min(500, fallbackBody.Length)]}");
        }
    }
    else
    {
        Console.WriteLine();
    }

    static bool TryGetText(JsonElement el, string prop, out string text)
    {
        text = "";
        if (!el.TryGetProperty(prop, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Null) return false;
        text = v.GetString() ?? "";
        return text.Length > 0;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// LM Studio IChunkEmbedder 実装
// ═══════════════════════════════════════════════════════════════════════════

sealed class LmStudioEmbedder(HttpClient http, string model) : IChunkEmbedder
{
    private int _dimensions;

    public int Dimensions => _dimensions;

    public async Task<int> ProbeAsync()
    {
        var vec = await EmbedSingleAsync("probe");
        _dimensions = vec.Length;
        return _dimensions;
    }

    public async Task<float[]> EmbedSingleAsync(string text)
    {
        var req = new { input = text, model };
        var resp = await http.PostAsJsonAsync("/v1/embeddings", req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>();
        return body!.Data[0].Embedding;
    }

    public async ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new float[texts.Count][];
        var req = new { input = texts.ToArray(), model };
        var resp = await http.PostAsJsonAsync("/v1/embeddings", req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
        for (int i = 0; i < body!.Data.Length; i++)
            result[body.Data[i].Index] = body.Data[i].Embedding;
        return result;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// JSON DTO
// ═══════════════════════════════════════════════════════════════════════════

record ModelsResponse
{
    [JsonPropertyName("data")]
    public ModelInfo[]? Data { get; init; }
}

record ModelInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

record EmbeddingResponse
{
    [JsonPropertyName("data")]
    public EmbeddingData[] Data { get; init; } = [];
}

record EmbeddingData
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("embedding")]
    public float[] Embedding { get; init; } = [];
}

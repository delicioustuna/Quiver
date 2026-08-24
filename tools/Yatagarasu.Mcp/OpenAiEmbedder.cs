// OpenAI 互換の /v1/embeddings API を呼び出す embedder。
// LM Studio・Ollama・OpenAI・Azure OpenAI など、同一仕様のサーバに対応する。
// Yatagarasu.Rag の IChunkEmbedder を実装しており、ベクトル検索パイプラインに組み込める。

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Yatagarasu.Rag;

namespace Yatagarasu.Mcp;

internal sealed class OpenAiEmbedder : IChunkEmbedder, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _profileId;
    private int _dimensions;

    public OpenAiEmbedder(EmbeddingConfig config)
    {
        // BaseAddress は末尾スラッシュ必須。
        // HttpClient の URI 結合規則上、末尾スラッシュが無いとパス部分が消える。
        // 例: "http://localhost:1234/v1" + "embeddings" → "http://localhost:1234/embeddings" (誤)
        //     "http://localhost:1234/v1/" + "embeddings" → "http://localhost:1234/v1/embeddings" (正)
        _http = new HttpClient { BaseAddress = new Uri(config.Endpoint.TrimEnd('/') + "/") };
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
        _model = config.Model;
        _profileId = $"openai-compatible:{_http.BaseAddress}|{_model}";
        _dimensions = config.Dimensions ?? 0;
    }

    public string ProfileId => _profileId;
    public int Dimensions => _dimensions;

    // ダミーテキストを投げて返却ベクトルの次元数を検出する
    public async Task<int> ProbeAsync(CancellationToken ct = default)
    {
        var result = await EmbedAsync(["probe"], ct);
        _dimensions = result[0].Length;
        return _dimensions;
    }

    public async ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var req = new { input = texts.ToArray(), model = _model };
        var resp = await _http.PostAsJsonAsync("embeddings", req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync(EmbeddingJsonContext.Default.EmbeddingResponse, ct);

        // レスポンスの data[].index はリクエスト順を保証しないため、index で正しい位置に配置する
        var result = new float[texts.Count][];
        foreach (var d in body!.Data)
            result[d.Index] = d.Embedding;
        return result;
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class EmbeddingResponse
{
    [JsonPropertyName("data")]
    public EmbeddingData[] Data { get; set; } = [];
}

internal sealed class EmbeddingData
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = [];
}

[JsonSerializable(typeof(EmbeddingResponse))]
internal partial class EmbeddingJsonContext : JsonSerializerContext;

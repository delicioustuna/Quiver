// MCP サーバの設定モデル。
// 設定ファイル (quiver-mcp.json) から DB パスや embedding エンドポイントを読み込む。
//
// 設定ファイルの探索順:
//   1. --config 引数で指定されたパス
//   2. カレントディレクトリの quiver-mcp.json
//   3. ~/.quiver/mcp.json (ユーザ共通設定)
// いずれも見つからなければ既定値 (カレントの data.quiver) で起動する。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiver.Mcp;

internal sealed class McpConfig
{
    // DB 名 → ファイルパスの辞書。v1 では "default" のみ使用。
    // v2 で use_database ツールにより名前指定切り替えを予定。
    // セキュリティ上、ツール経由で任意パスを受け取らず、ここに事前登録されたものだけを使う。
    [JsonPropertyName("databases")]
    public Dictionary<string, string> Databases { get; set; } = new() { ["default"] = "data.quiver" };

    [JsonPropertyName("embedding")]
    public EmbeddingConfig? Embedding { get; set; }

    public static McpConfig Load(string? configPath)
    {
        configPath ??= FindConfigFile();
        if (configPath is null || !File.Exists(configPath))
            return new McpConfig();

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize(json, McpConfigJsonContext.Default.McpConfig)
               ?? new McpConfig();
    }

    private static string? FindConfigFile()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "quiver-mcp.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".quiver", "mcp.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

// OpenAI 互換の /v1/embeddings エンドポイント設定。
// LM Studio / Ollama / OpenAI / Azure OpenAI いずれも同じ形式で利用可能。
internal sealed class EmbeddingConfig
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "http://localhost:1234/v1";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "text-embedding-nomic-embed-text-v1.5";

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    // null の場合、起動時に probe リクエストを送って次元数を自動検出する
    [JsonPropertyName("dimensions")]
    public int? Dimensions { get; set; }
}

[JsonSerializable(typeof(McpConfig))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class McpConfigJsonContext : JsonSerializerContext;

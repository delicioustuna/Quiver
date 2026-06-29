// Quiver MCP サーバのエントリポイント。
// stdio トランスポートで JSON-RPC を受け取り、schema / traverse ツールを提供する。
// LM Studio・Claude Code 等の MCP クライアントから呼び出される想定。

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol.Server;
using Quiver;
using Quiver.Mcp;

var configPath = args.Length > 0 && args[0] is "--config" && args.Length > 1
    ? args[1]
    : null;

var config = McpConfig.Load(configPath);
var dbPath = config.Databases.GetValueOrDefault("default")
    ?? throw new InvalidOperationException("No 'default' database configured in quiver-mcp.json.");

// DB はプロセス起動時に 1 度だけ Open し、全リクエストで共有する (in-process 組み込み DB)
var db = GraphDatabase.Open(dbPath);

OpenAiEmbedder? embedder = null;
if (config.Embedding is { } embConfig)
{
    embedder = new OpenAiEmbedder(embConfig);
    // dimensions が設定ファイルで指定されていなければ、probe リクエストで自動検出する
    if (embedder.Dimensions == 0)
        await embedder.ProbeAsync();
}

var ctx = new McpContext(db, embedder);

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio トランスポートは stdout を JSON-RPC 専用に使う。
// ホストのログが stdout に混ざるとクライアントが JSON パースに失敗するため、
// 全ログを stderr へ振り向ける。
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// McpContext を DI に登録すると、ツールクラスのメソッド引数に自動注入される
builder.Services.AddSingleton(ctx);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

using var app = builder.Build();
await app.RunAsync();

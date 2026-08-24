// ツールメソッドに DI で注入されるコンテキスト。
// DB と Embedder の寿命はプロセスと一致する。

namespace Yatagarasu.Mcp;

internal sealed class McpContext : IDisposable
{
    public YatagarasuDatabase Db { get; }

    // embedding 未設定の場合は null — vector 検索は使えないが FTS/label/id は利用可能
    public OpenAiEmbedder? Embedder { get; }

    public McpContext(YatagarasuDatabase db, OpenAiEmbedder? embedder)
    {
        Db = db;
        Embedder = embedder;
    }

    public void Dispose()
    {
        Embedder?.Dispose();
        Db.Dispose();
    }
}

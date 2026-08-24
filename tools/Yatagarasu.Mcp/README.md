# Yatagarasu.Mcp

Yatagarasu グラフデータベースを MCP (Model Context Protocol) サーバとして公開するコンソールアプリケーション。

LLM が **1 回のツール呼び出し** で全文検索・ベクトル検索とグラフ走査を組み合わせたクエリを実行できる。

## 特徴

- **グラフ走査を 1 呼び出しで完結** — 検索で起点Vertexを見つけ、エッジを最大 2-hop 辿り、中間フィルタを適用して結果を返す。LLM とサーバの往復を最小化する。
- **4 種類の検索起点** — 全文検索 (fulltext)、ベクトル検索 (vector)、ラベル指定 (label)、ID 直接指定 (id)
- **stdio トランスポート** — LM Studio / Claude Code / Claude Desktop など主要な MCP クライアントに対応
- **ローカル LLM 向けに最適化** — ツールパラメータを flat な個別引数にし、JSON ダブルエンコードを不要にしている

## 前提条件

- .NET 10 SDK
- Yatagarasu 形式の DB ファイル (`.yata`)
- (ベクトル検索を使う場合) OpenAI 互換の embedding エンドポイント (LM Studio / Ollama 等)

## 設定ファイル

`yatagarasu-mcp.json` を作成する。探索順:

1. `--config` 引数で指定したパス
2. カレントディレクトリの `yatagarasu-mcp.json`
3. `~/.yata/mcp.json`

```json
{
  "databases": {
    "default": "D:/data/knowledge.yata"
  },
  "embedding": {
    "endpoint": "http://localhost:1234/v1",
    "model": "text-embedding-nomic-embed-text-v1.5",
    "apiKey": null,
    "dimensions": null
  }
}
```

| フィールド | 説明 |
|---|---|
| `databases` | 名前 → DB ファイルパスの辞書。v1 では `"default"` のみ使用 |
| `embedding.endpoint` | OpenAI 互換の `/v1/embeddings` ベース URL |
| `embedding.model` | embedding モデル名 |
| `embedding.apiKey` | API キー (ローカルサーバなら省略可) |
| `embedding.dimensions` | ベクトル次元数。`null` なら起動時に自動検出 |

`embedding` セクション自体を省略すると、ベクトル検索は無効になる (全文検索・ラベル・ID 検索は利用可能)。

## 起動方法

```bash
# ソースから直接実行
dotnet run --project tools/Yatagarasu.Mcp -- --config path/to/yatagarasu-mcp.json

# ビルド済みバイナリで実行
dotnet publish tools/Yatagarasu.Mcp -c Release -o ./publish
./publish/Yatagarasu.Mcp --config path/to/yatagarasu-mcp.json
```

## MCP クライアント設定

### LM Studio

Settings > MCP Servers に以下を追加:

```json
{
  "mcpServers": {
    "yatagarasu": {
      "command": "dotnet",
      "args": [
        "run", "--project", "D:/csharp/Yatagarasu/tools/Yatagarasu.Mcp",
        "--", "--config", "D:/path/to/yatagarasu-mcp.json"
      ]
    }
  }
}
```

### Claude Code

```bash
claude mcp add yatagarasu -- \
  dotnet run --project D:/csharp/Yatagarasu/tools/Yatagarasu.Mcp \
  -- --config D:/path/to/yatagarasu-mcp.json
```

### Claude Desktop

`claude_desktop_config.json` に追加:

```json
{
  "mcpServers": {
    "yatagarasu": {
      "command": "dotnet",
      "args": [
        "run", "--project", "D:/csharp/Yatagarasu/tools/Yatagarasu.Mcp",
        "--", "--config", "D:/path/to/yatagarasu-mcp.json"
      ]
    }
  }
}
```

## ツール仕様

### `schema`

パラメータなし。DB のグラフスキーマを返す。LLM は最初にこれを呼び、`traverse` の引数を組み立てる。

返却例:

```json
{
  "vertices": [
    {
      "label": "Document",
      "properties": { "title": "string", "year": "long" },
      "indexes": { "fulltext": ["title"] }
    },
    {
      "label": "Author",
      "properties": { "name": "string" }
    }
  ],
  "edges": [
    { "label": "CITES" },
    { "label": "AUTHORED_BY" }
  ],
  "vectorIndexes": [
    { "name": "vec_doc", "dimensions": 768, "metric": "cosine" }
  ]
}
```

### `traverse`

検索 + グラフ走査を 1 回で実行する。

| パラメータ | 型 | 必須 | 説明 |
|---|---|---|---|
| `startType` | string | はい | 検索方法: `fulltext`, `vector`, `label`, `id` |
| `query` | string | | 検索テキスト (fulltext / vector で必須) |
| `label` | string | | ラベルで絞り込み |
| `ids` | string | | カンマ区切りのVertex ID (id 検索用) |
| `startLimit` | int | | 起点Vertexの最大数 (既定 10) |
| `hop1Edge` | string | | 1 段目のエッジタイプ |
| `hop1Direction` | string | | 1 段目の方向: `out`, `in`, `both` (既定 `both`) |
| `hop1Filter` | string | | 1 段目のプロパティフィルタ (JSON) |
| `hop2Edge` | string | | 2 段目のエッジタイプ |
| `hop2Direction` | string | | 2 段目の方向 |
| `hop2Filter` | string | | 2 段目のプロパティフィルタ (JSON) |
| `returnMode` | string | | `terminal` (既定) または `path` |
| `returnProperties` | string | | カンマ区切りで返却するプロパティ名 (既定: 全て) |
| `returnLimit` | int | | 返却件数の上限 (既定 100) |

フィルタの書式:

```json
{"year": {"gte": 2020}}
{"status": {"in": ["published", "preprint"]}}
{"title": {"contains": "graph"}}
{"name": "Keanu"}
```

対応演算子: `eq` (省略可), `neq`, `gt`, `gte`, `lt`, `lte`, `contains`, `in`

## 利用例

「"graph database" を含む 2020 年以降の文書が引用している文書の著者は?」:

```
LLM → schema()
LLM ← { vertices: [...], edges: [...] }

LLM → traverse(
         startType: "fulltext",
         query: "graph database",
         label: "Document",
         hop1Edge: "CITES",
         hop1Direction: "out",
         hop1Filter: "{\"year\":{\"gte\":2020}}",
         hop2Edge: "AUTHORED_BY",
         hop2Direction: "out",
         returnMode: "terminal",
         returnProperties: "name",
         returnLimit: 20
       )
LLM ← [{ "id": "42", "label": "Author", "name": "Alice" }, ...]
```

schema は会話冒頭で 1 回呼べばよいので、実質 **traverse 1 回** で回答が得られる。

## アーキテクチャ

```
MCP クライアント (LM Studio / Claude)
    │  stdio (JSON-RPC)
    ▼
Yatagarasu.Mcp プロセス
    ├── schema ツール ──→ ISchemaCatalog / IndexDefinition
    └── traverse ツール ─→ IReadTransaction
                           IChunkEmbedder (vector 検索時のみ)
    │
    ▼
Yatagarasu エンジン (in-process)
    └── *.yata ファイル
```

DB は MCP プロセスに in-process でロードされる。ネットワーク通信は embedding API 呼び出し (ベクトル検索時) のみ。

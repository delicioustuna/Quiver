# MCP サーバ計画書 (Quiver.Mcp)

> 起案日: 2026-06-29。

## 背景と動機

Quiver は in-process 組み込み DB であり「サーバプロセス / ネットワークプロトコル」は非目標だが、
MCP (Model Context Protocol) サーバは薄いプロセスラッパーに過ぎず、DB は MCP プロセスに in-process で
ロードされるため非目標と矛盾しない。

単なるベクトル検索 MCP なら既存のものと差別化できない。
**グラフ走査を 1 ツール呼び出しで完結させる** ことが Quiver MCP 最大の独自価値となる。
FTS / ベクトル検索で起点を取り、グラフエッジを最大 2-hop 辿り、中間フィルタを適用して
最終結果を返す — これを LLM は 1 回の JSON ツール呼び出しで宣言的に表現できる。

## 設計判断

### ツール構成

| フェーズ | ツール | 用途 |
|---|---|---|
| v1 | `schema` | ラベル・エッジ・プロパティ・インデックスの取得 |
| v1 | `traverse` | 検索起点 + 最大 2-hop グラフ走査 |
| v2 | `use_database` | 事前登録 DB の名前指定切り替え |
| v2 | `ingest` | 文書取り込み (書き込み系) |

v1 は読み取り専用。書き込み系は v2 で追加するが、拡張性を設計段階で確保する。

### `schema` ツール

パラメータなし。現在接続中の DB のスキーマを返す。
LLM はこのレスポンスを見て `traverse` のパラメータを組み立てる。

```jsonc
// 返却例
{
  "nodes": [
    {
      "label": "Document",
      "properties": { "title": "string", "year": "int", "abstract": "string" },
      "indexes": { "fulltext": ["title", "abstract"], "vector": ["embedding"] }
    },
    { "label": "Author", "properties": { "name": "string" } }
  ],
  "edges": [
    { "label": "CITES",       "from": "Document", "to": "Document" },
    { "label": "AUTHORED_BY", "from": "Document", "to": "Author" }
  ]
}
```

### `traverse` ツール

検索起点 → ホップ → 返却を 1 呼び出しで宣言する。

```jsonc
{
  "start": {
    "type": "fulltext | vector | label | id",
    // --- type ごとに使用するフィールド ---
    "query": "...",              // fulltext / vector
    "label": "Document",         // 任意: ラベル絞り込み (全 type で利用可)
    "ids": ["..."],              // id
    "filter": { ... },           // label 時のプロパティ条件
    "limit": 10                  // fulltext / vector の初期取得数
  },
  "hops": [                      // 0〜2 段
    {
      "edge": "CITES",
      "direction": "out | in | both",
      "filter": { "year": { "gte": 2020 } }
    },
    {
      "edge": "AUTHORED_BY",
      "direction": "out"
    }
  ],
  "return": {
    "mode": "terminal | path",
    "properties": ["name"],
    "limit": 20                  // 最終結果の上限 (最終ステップに適用)
  }
}
```

**start.type の内部変換:**

| type | 内部動作 |
|---|---|
| `fulltext` | `query` で FTS 検索 → 上位 `limit` 件のノード ID を取得 |
| `vector` | `query` テキストを設定済み embedder でベクトル化 → KNN 検索 |
| `label` | ラベル一致 + `filter` 条件でノードを列挙 |
| `id` | `ids` で直接指定 |

type による分岐は MCP サーバ内部で機械的に変換する。LLM は type を選ぶだけ。

**フィルタ演算子:**

`eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`, `in`

```jsonc
// 例
{ "year": { "gte": 2020 } }
{ "status": { "in": ["published", "preprint"] } }
{ "title": { "contains": "graph" } }
```

**返却モード:**

```jsonc
// mode: "terminal" → 最終ノードのみ
[
  { "id": "author-1", "label": "Author", "name": "田中" },
  { "id": "author-2", "label": "Author", "name": "鈴木" }
]

// mode: "path" → 経由ノード含む (各パスの全ノードを配列で返却)
[
  { "path": [
    { "id": "doc-A", "label": "Document", "title": "○○の研究" },
    { "id": "doc-B", "label": "Document", "title": "基礎理論" },
    { "id": "author-1", "label": "Author", "name": "田中" }
  ]}
]
```

### 設定ファイル

`quiver-mcp.json` (パスは起動引数 `--config` で指定可):

```jsonc
{
  // 名前付き DB。v1 では "default" のみ使用。
  // v2 で use_database ツールにより切り替え可能にする。
  // 任意パスをツール経由で受けることはしない (セキュリティ)。
  "databases": {
    "default": "D:/data/knowledge.quiver"
  },

  // 埋め込み (vector 検索時に使用)
  // OpenAI 互換 /v1/embeddings エンドポイント
  // (LM Studio / Ollama / OpenAI / Azure OpenAI 共通)
  "embedding": {
    "endpoint": "http://localhost:1234/v1",
    "model": "text-embedding-nomic-embed-text-v1.5",
    "apiKey": null,
    "dimensions": 768       // null なら probe で自動検出
  }
}
```

### アセンブリ構成

| アセンブリ | 役割 |
|---|---|
| `Quiver.Mcp` | MCP サーバ本体 (コンソールアプリ)。stdio トランスポート。設定読み込み、ツール実装、OpenAI 互換 embedder を内蔵 |

依存: `Quiver` (エンジン), `Quiver.Rag` (RAG スキーマ), `ModelContextProtocol` (MCP SDK)

### トランスポート

stdio。Claude Code / Claude Desktop との連携に最も互換性が高い。

## 利用例

LLM が「○○を含む 2020 年以降の文書が引用している文書の著者名」を回答する場合:

```
LLM → schema()
LLM ← { nodes: [...], edges: [...] }       // ラベル・エッジ構造を把握

LLM → traverse({
  start: { type: "fulltext", query: "○○", label: "Document" },
  hops: [
    { edge: "CITES", direction: "out", filter: { year: { gte: 2020 } } },
    { edge: "AUTHORED_BY", direction: "out" }
  ],
  return: { mode: "terminal", properties: ["name"], limit: 20 }
})
LLM ← [{ id: "author-1", label: "Author", name: "田中" }, ...]
```

2 往復で完結。schema は会話冒頭で 1 度呼べばキャッシュ可能なので、
実質的に **traverse 1 回** で任意のグラフ検索質問に回答できる。

## タスク列

| ID | 内容 | 依存 | 状態 |
|---|---|---|---|
| MCP-1 | プロジェクト雛形: `Quiver.Mcp` csproj + `ModelContextProtocol` 依存 + stdio ホスト起動 | — | ✅ |
| MCP-2 | 設定ファイル読み込み (`quiver-mcp.json` + `--config` 引数) | MCP-1 | ✅ |
| MCP-3 | OpenAI 互換 embedder (`/v1/embeddings`) — サンドボックスの `LmStudioEmbedder` をベースに | MCP-1 | ✅ |
| MCP-4 | `schema` ツール実装 | MCP-1 | ✅ |
| MCP-5 | `traverse` ツール実装 — start (全 type) + hops (0-2, 方向 both 対応) + filter + return (terminal/path) + limit | MCP-1, MCP-3, MCP-4 | ✅ |
| MCP-6 | テスト + Claude Code からの実動作検証 | MCP-5 | ✅ |

並列性: MCP-2, MCP-3, MCP-4 は MCP-1 完了後に独立並行可。MCP-5 は全てに依存。

## MCP クライアント設定

### LM Studio

LM Studio 0.3+ の MCP 設定 (Settings → MCP Servers):

```jsonc
{
  "mcpServers": {
    "quiver": {
      "command": "dotnet",
      "args": [
        "run", "--project", "D:/csharp/Quiver/tools/Quiver.Mcp",
        "--", "--config", "D:/path/to/quiver-mcp.json"
      ]
    }
  }
}
```

### Claude Code

`claude mcp add` で登録:

```bash
claude mcp add quiver -- dotnet run --project D:/csharp/Quiver/tools/Quiver.Mcp -- --config D:/path/to/quiver-mcp.json
```

### quiver-mcp.json 例

```jsonc
{
  "databases": {
    "default": "D:/data/knowledge.quiver"
  },
  // LM Studio の embedding サーバを使う場合
  "embedding": {
    "endpoint": "http://localhost:1234/v1",
    "model": "text-embedding-nomic-embed-text-v1.5"
  }
}
```

embedding 未設定でも FTS / label / id 検索は利用可能。vector 検索のみ embedding が必要。

# Quiver

[![CI](https://github.com/delicioustuna/Quiver/actions/workflows/ci.yml/badge.svg)](https://github.com/delicioustuna/Quiver/actions/workflows/ci.yml)
[![AOT publish smoke](https://github.com/delicioustuna/Quiver/actions/workflows/aot.yml/badge.svg)](https://github.com/delicioustuna/Quiver/actions/workflows/aot.yml)

Quiver は組み込みのグラフデータベース + ベクトル検索 + 全文検索の統合エンジンです。
VertexとEdgeをプロパティ付きで単一ファイルに永続化し、Source Generator による型安全な CRUD と
Fluent な API　によるグラフトラバーサルが実行可能です。 
コアパッケージは依存パッケージゼロの純 C# 実装です。アンマネージド依存がなく、NativeAOT に対応します。

## クイックスタート

```csharp
var g = tx.G(db.Schema);
var known = g.Vertices<Person>()
             .Has(p => p.Name, "Alice")
             .Knows()
             .Has(p => p.Age, P.Lt(30L))
             .ToList();
```

```csharp
[Vertex]
public partial class Person // Source Generator を使う場合 partial 指定が必須です。
{
    [Indexed]
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

[Edge<Person, Person>]
public partial class Knows
{
    [Property]
    public string Since { get; set; } = "";
}
```

* `Quiver` パッケージを参照すると、属性と Source Generator も同梱されます。
* `ImplicitUsings` が有効なプロジェクト（新規テンプレート既定）では `Quiver` / `Quiver.Api` の `using` も自動で入るため、 `using` を省略できます（ドロップイン）。
* 個人利用が目的のため公開バージョンは v0.1.0 (pre-release) としています。

> 文字列キー指定のローレベル / 型なしトラバーサル、Match DSL（宣言的パターンマッチ）、
> `tx.CreateVertex` などの低レイヤ API は [docs/development.md](docs/design/development.md#ローレベル--型なし-api) を参照。

## 特徴

- 純C#
- サーバープロセス不要
- Source Generatorによる型安全なAPI生成
- 依存パッケージゼロの純 C# コア、アンマネージド依存なし、NativeAOT 対応
- KNN（ベクトル検索）とグラフトラバーサル・BM25 全文検索を組み合わせたハイブリッド検索

## ユースケース: ローカル RAG バックエンド

ベクトル検索（KNN）・BM25 全文検索とのハイブリッド検索・グラフ走査（ヒットしたチャンクの
前後文脈や親文書への連結）・メタデータフィルタを 1 ファイル・1 プロセス・外部依存なしで
組み合わせられるため、ローカル RAG（検索拡張生成）のバックエンドに適しています。

> RAG 用スキーマ層 [`Quiver.Rag`](src/Quiver.Rag/)（文書取込・チャンキング・再取込・hybrid 検索 + 
> graph expansion）を同梱しています。利用例は [`samples/Quiver.Samples.Rag`](samples/Quiver.Samples.Rag/)、
> レシピは [cookbook の「ローカル RAG」](docs/cookbook.md) を参照してください。


## ベンチマーク（参考値）

| 操作 | 実測 |
|---|---|
| Vertex作成（単一 tx 償却） | ~3.5–4 µs/op（~250K ops/s） |
| Vertex作成 + プロパティ設定（同上） | ~6 µs/op |
| リレーション作成（同上） | ~7 µs/op（~140K ops/s） |
| 単発 durable commit（1 op = 1 commit、単一スレッド） | ~1.0 ms/commit（WAL flush 律速） |
| 1-hop scan（degree 100、隣接ブロック） | ~0.35 µs（~3.5 ns/edge） |
| 1-hop scan（degree 100、索引なし linked-list） | ~11 µs（~0.11 µs/edge） |
| BFS 2-hop（ハブ degree 100、leaf 10,000、隣接ブロック） | ~0.037 ms |
| 1-hop クエリ（`g.Vertex().Out()`、degree 100、隣接ブロック） | ~4.2 µs/query（~42 ns/edge） |
| BulkLoader（10 万 edge） | 通常 TX（batch 1000）比 ~11.8× |
| HNSW true recall@10（N=10k、dim=384、既定 M=32/efC=400） | 0.950 |
| HNSW true recall@10（同上、高品質 M=32/efC=400） | 0.950（30% 削除後 0.985） |

> AMD Ryzen 7 5700X / .NET 10 / best-of-N の in-process Stopwatch による参考計測値。
> 計測条件・詳細・追加ベンチは [docs/development.md](docs/design/development.md#性能詳細計測) を参照。

## 制限事項 (Limitations)

Quiver は組み込み用途に最適化されたエンジンであり、以下の制限があります。
詳細は [docs/spec/08_known_limits.md](docs/spec/08_known_limits.md) を参照してください。

| 制限 | 概要 | 緩和策 |
|---|---|---|
| **単一ライタ** | 書き込みトランザクションは同時に 1 つのみ。トランザクションはスレッドアフィン | アプリ側で書き込みゲート (`SemaphoreSlim(1,1)`) または専用ライタスレッドを使用 |
| **In-Process のみ** | サーバモード・ネットワークアクセスなし。1 プロセスが排他的にファイルを開く | マルチプロセスが必要なら上位に gRPC/HTTP ラッパを配置 |
| **自動マイグレーションなし** | フォーマットバージョン不一致で例外スロー。in-place 自動変換パスは存在しない | ソースデータから再構築。1.x 内ではフォーマット固定 |
| **HNSW 上書き** | ベクトル上書き時にグラフトポロジを再リンクしない（検索品質がわずかに劣化しうる） | tombstone 超過で自動 rebuild。頻繁更新時はVertex削除→再作成 |

## その他の資料

### 詳細ドキュメント

| 資料 | 説明 |
|---|---|
| [Getting Started](docs/api/getting-started.md) | まずはここから |
| [Concepts](docs/api/concepts/index.md) | Vertex/Edge, Transaction, Traversal, MERGE, KNN, Backends など |
| [Tutorials](docs/api/tutorials/index.md) | チュートリアル |
| [Cookbook](docs/cookbook.md)| 典型ユースケースのレシピ集 |
| [運用ガイド](docs/operations/README.md)| quickstart, backup/restore, performance tuning, recoveryなど |
| [用語辞書](docs/glossary.md) | API に登場する概念の定義・用語集 |
| [アーキテクチャ概要](docs/architecture.md) | 全体構成、データフロー、運用上の注意 |
| [アーキテクチャ図](docs/architecture-diagrams.md)| Mermaid による本プロジェクトの構成図集 |

### 実装サンプル

| サンプル | 内容 |
|---|---|
| [`Quiver.Samples.Crud`](samples/Quiver.Samples.Crud/) | 基本 CRUD（Vertex / リレーション / プロパティ） |
| [`Quiver.Samples.SourceGen`](samples/Quiver.Samples.SourceGen/) | `[Vertex]` / `[Edge]` 属性ベースの型付き CRUD |
| [`Quiver.Samples.Traversal`](samples/Quiver.Samples.Traversal/) | 多段トラバーサル、フィルタ、可変長 repeat、集約、cursor |
| [`Quiver.Samples.Match`](samples/Quiver.Samples.Match/) | Match DSL によるパターンマッチと MERGE / UPSERT |
| [`Quiver.Samples.Vector`](samples/Quiver.Samples.Vector/) | KNN 起点トラバーサル + graph-first ハイブリッド検索 |
| [`Quiver.Samples.Hosting`](samples/Quiver.Samples.Hosting/) | `Microsoft.Extensions.Hosting` 連携（DI） |
| [`Quiver.Samples.Observability`](samples/Quiver.Samples.Observability/) | OpenTelemetry による計測 |
| [`Quiver.Samples.Migration`](samples/Quiver.Samples.Migration/) | スキーマ / データマイグレーション |
| [`Quiver.Samples.Rag`](samples/Quiver.Samples.Rag/) | `Quiver.Rag` で文書取込 → hybrid 検索 → graph expansion |

## ライセンス

[MIT License](LICENSE) です。

# Quiver

[![CI](https://github.com/delicioustuna/Quiver/actions/workflows/ci.yml/badge.svg)](https://github.com/delicioustuna/Quiver/actions/workflows/ci.yml)
[![AOT publish smoke](https://github.com/delicioustuna/Quiver/actions/workflows/aot.yml/badge.svg)](https://github.com/delicioustuna/Quiver/actions/workflows/aot.yml)

Quiver は Pure C# で実装された、組み込み（in-process）のグラフデータベースエンジンです。
ノードとリレーションシップをプロパティ付きで単一ファイルに永続化し、型安全な CRUD と
Fluent なグラフトラバーサルを提供します。アンマネージド依存はなく、Windows / Linux / macOS
（x64, ARM64）で動作し、NativeAOT に対応します。

現在のバージョン: **v0.1.0**（pre-release。`0.x` のため公開 API はまだ安定保証の対象外です）

## 特徴

- **Pure C#** — アンマネージド依存なし。Windows / Linux / macOS（x64, ARM64）で動作
- **組み込み + 単一ファイル** — 静止時は `*.quiver` 1 ファイル。サーバープロセス不要
- **型安全 API** — Source Generator が `[Node]` / `[Relationship]` モデルから CRUD と型保存トラバーサルを生成
- **NativeAOT 対応** — 単一バイナリとして配布可能。リフレクション不使用
- **ゼロアロケーションホットパス** — `Span<T>` / `ref struct` でヒープ確保を排除
- **WAL + クラッシュリカバリ** — Write-Ahead Log の page-image replay でコミット済みデータを完全復元
- **B+Tree インデックス** — 完全一致・範囲検索
- **隣接ブロックストレージ** — ページ連続配置による高速な隣接リスト走査
- **クエリ最適化** — ヒストグラム統計 + ルールベース Optimizer でスキャン順序を自動選択
- **ストリーミング** — `AsCursor()` / `AsEnumerable()` で大量結果をメモリを抑えて逐次処理
- **グラフアルゴリズム** — 重み付き最短経路（Dijkstra / A*）・BFS・可変長トラバーサル・パターンマッチ
- **ベクトル検索** — KNN とグラフトラバーサル・BM25 全文検索を組み合わせたハイブリッド検索（コアに内蔵）

## 主なユースケース: ローカル RAG バックエンド

ベクトル検索（KNN）・BM25 全文検索とのハイブリッド検索・グラフ走査（ヒットしたチャンクの
前後文脈や親文書への連結）・メタデータフィルタを 1 ファイル・1 プロセス・外部依存なしで
組み合わせられるため、ローカル RAG（検索拡張生成）のバックエンドに適しています。

RAG 用スキーマ層 [`Quiver.Rag`](src/Quiver.Rag/)（文書取込・チャンキング・再取込・hybrid 検索 +
graph expansion）を同梱しています。利用例は [`samples/Quiver.Samples.Rag`](samples/Quiver.Samples.Rag/)、
レシピは [cookbook の「ローカル RAG」](docs/cookbook.md) を参照してください。

## クイックスタート

`Quiver` パッケージを参照すると、属性と Source Generator も同梱されます。`ImplicitUsings` が
有効なプロジェクト（新規テンプレート既定）では `Quiver` / `Quiver.Api` の `using` も自動で入るため、
下記の `using` を省略できます（ドロップイン）。

### 型安全な CRUD（Source Generator）

`[Node]` / `[Relationship]` 属性を付けた `partial` クラスを定義すると、CRUD と
`FindBy{Property}` メソッドが生成されます。ラベル・インデックス名は省略可（クラス名・
プロパティ名から自動生成。リネーム耐性が必要なら明示指定）。

```csharp
using Quiver.Api;

[Node]                       // label = "Person"
public partial class Person
{
    [Indexed]                // indexName = "idx_person_name"
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

[Relationship<Person, Person>("KNOWS")]
public partial class Knows
{
    [Property]
    public string Since { get; set; } = "";
}
```

```csharp
using Quiver;          // GraphDatabase
using Quiver.Api;      // 生成された CRUD / トラバーサル API

using var db = GraphDatabase.Open("./mygraph");
using var tx = db.BeginTransaction();

var aliceId = Person.InsertIndexed(tx, new Person { Name = "Alice", Age = 30 });
var bobId   = Person.InsertIndexed(tx, new Person { Name = "Bob",   Age = 25 });

// インデックス検索（生成された FindBy* メソッド）
var found = Person.FindByName(tx, "Alice");        // → List<(NodeId, Person)>
var alice = Person.Load(tx, aliceId);

// リレーションシップ（プロパティ付き）
var relId = Knows.Insert(tx, aliceId, bobId, new Knows { Since = "2024-01" });

Person.Update(tx, aliceId, alice with { Age = 31 });
tx.Commit();
```

### Fluent Traversal API（グラフトラバーサル）

メソッドチェーンでグラフを辿ります。`[Relationship]` から生成される型保存糖衣
（リレーション型名そのもののメソッド）を使うと、ホップ間で要素型を保ったまま辿れます。

```csharp
var g = tx.G(db.Schema);

// 型付きトラバーサル: Knows() が Person 型を保ったまま終点へ辿る
var known = g.Nodes<Person>()
             .Has(p => p.Name, "Alice")
             .Knows()                       // Person -KNOWS-> Person（型を保持）
             .Has(p => p.Age, P.Lt(30L))
             .ToList();                      // → List<Person>（自動ロード）

// LINQ ライクな式ツリー述語（比較 / && / 同一キー || / StartsWith など）
var adults = g.Nodes<Person>()
              .Where(p => p.Age > 25 && p.Name.StartsWith("A"))
              .ToList();

// エッジ（リレーションシップ）プロパティでの絞り込み
var recent = g.Nodes<Person>()
              .Where(p => p.Name == "Alice")
              .Knows(e => e.Since == "2024-01")   // Knows エッジの Since で絞り込み
              .ToList();
```

> 文字列キー指定のローレベル / 型なしトラバーサル、Match DSL（宣言的パターンマッチ）、
> `tx.CreateNode` などの低レイヤ API は [docs/development.md](docs/development.md#ローレベル--型なし-api) を参照。

## 性能（基本計測）

AMD Ryzen 7 5700X / .NET 10 / best-of-N の in-process Stopwatch による参考計測値（2026-06-09）。
計測条件・詳細・追加ベンチは [docs/development.md](docs/development.md#性能詳細計測) を参照。

| 操作 | 実測 (2026-06-09) |
|---|---|
| ノード作成（単一 tx 償却） | ~3.5–4 µs/op（~250K ops/s） |
| ノード作成 + プロパティ設定（同上） | ~6 µs/op |
| リレーション作成（同上） | ~7 µs/op（~140K ops/s） |
| 単発 durable commit（1 op = 1 commit、単一スレッド） | ~1.0 ms/commit（WAL flush 律速） |
| 1-hop scan（degree 100、隣接ブロック） | ~0.35 µs（~3.5 ns/edge） |
| 1-hop scan（degree 100、索引なし linked-list） | ~11 µs（~0.11 µs/edge） |
| BFS 2-hop（ハブ degree 100、leaf 10,000、隣接ブロック） | ~0.037 ms |
| 1-hop クエリ（`g.Node().Out()`、degree 100、隣接ブロック） | ~4.2 µs/query（~42 ns/edge） |
| BulkLoader（10 万 edge） | 通常 TX（batch 1000）比 ~11.8× |

## 制限事項 (Limitations)

Quiver は組み込み用途に最適化されたエンジンであり、以下の制限があります。
詳細は [docs/spec/08_known_limits.md](docs/spec/08_known_limits.md) を参照してください。

| 制限 | 概要 | 緩和策 |
|---|---|---|
| **単一ライタ** | 書き込みトランザクションは同時に 1 つのみ。トランザクションはスレッドアフィン | アプリ側で書き込みゲート (`SemaphoreSlim(1,1)`) または専用ライタスレッドを使用 |
| **In-Process のみ** | サーバモード・ネットワークアクセスなし。1 プロセスが排他的にファイルを開く | マルチプロセスが必要なら上位に gRPC/HTTP ラッパを配置 |
| **自動マイグレーションなし** | フォーマットバージョン不一致で例外スロー。in-place 自動変換パスは存在しない | ソースデータから再構築。1.x 内ではフォーマット固定 |
| **HNSW 上書き** | ベクトル上書き時にグラフトポロジを再リンクしない（検索品質がわずかに劣化しうる） | tombstone 超過で自動 rebuild。頻繁更新時はノード削除→再作成 |

## ドキュメント

> ドキュメント・サンプルは順次整備中です。

- [Getting Started](docs/api/getting-started.md)
- [Concepts](docs/api/concepts/index.md) — Node/Relationship、Transaction、Traversal、MERGE、KNN、Backends
- [Tutorials](docs/api/tutorials/index.md)
- [Cookbook](docs/cookbook.md) — よく使う典型レシピ集
- [運用ガイド (Operations)](docs/operations/README.md) — quickstart / backup・restore / performance tuning / recovery
- [開発者向けドキュメント](docs/development.md) — アーキテクチャ / 依存関係 / ストレージ仕様 / 性能詳細 / バージョニング
- [仕様ドキュメント](docs/spec/) — ストレージ・WAL・トランザクション・クエリ・ベクトル・全文検索の仕様

## サンプル

[`samples/`](samples/) 配下に機能別の独立サンプルプロジェクトがある。`dotnet run --project samples/<name>` で完走する。

| サンプル | 内容 |
|---|---|
| [`Quiver.Samples.Crud`](samples/Quiver.Samples.Crud/) | 基本 CRUD（ノード / リレーション / プロパティ） |
| [`Quiver.Samples.SourceGen`](samples/Quiver.Samples.SourceGen/) | `[Node]` / `[Relationship]` 属性ベースの型付き CRUD |
| [`Quiver.Samples.Traversal`](samples/Quiver.Samples.Traversal/) | 多段トラバーサル、フィルタ、可変長 repeat、集約、cursor |
| [`Quiver.Samples.Match`](samples/Quiver.Samples.Match/) | Match DSL によるパターンマッチと MERGE / UPSERT |
| [`Quiver.Samples.Vector`](samples/Quiver.Samples.Vector/) | KNN 起点トラバーサル + graph-first ハイブリッド検索 |
| [`Quiver.Samples.Hosting`](samples/Quiver.Samples.Hosting/) | `Microsoft.Extensions.Hosting` 連携（DI） |
| [`Quiver.Samples.Observability`](samples/Quiver.Samples.Observability/) | OpenTelemetry による計測 |
| [`Quiver.Samples.Migration`](samples/Quiver.Samples.Migration/) | スキーマ / データマイグレーション |
| [`Quiver.Samples.Rag`](samples/Quiver.Samples.Rag/) | `Quiver.Rag` で文書取込 → hybrid 検索 → graph expansion |

## ライセンス

[MIT License](LICENSE)

# Quiver

<!-- CI-1: GitHub remote 設定後、owner を実リポジトリに合わせて有効化する。 -->
[![CI](https://github.com/delicioustuna/Quiver/actions/workflows/ci.yml/badge.svg)](https://github.com/delicioustuna/Quiver/actions/workflows/ci.yml)
[![AOT publish smoke](https://github.com/delicioustuna/Quiver/actions/workflows/aot.yml/badge.svg)](https://github.com/delicioustuna/Quiver/actions/workflows/aot.yml)

Pure C# で実装するグラフデータベースエンジン。Amazon Neptune や Apache TinkerPop のような本格的なグラフ DB のコア層を、マネージドコードのみで構築することを目標とする。

## 特徴

- **Pure C#** — アンマネージド依存なし。Windows / Linux / macOS (x64, ARM64) で動作
- **NativeAOT 対応** — 単一バイナリとして配布可能。リフレクション不使用
- **ゼロアロケーションホットパス** — `Span<T>` / `ref struct` でヒープ確保を排除
- **Volcano 型クエリエンジン** — 物理演算子を手書きで合成してクエリを実行
- **WAL + クラッシュリカバリ** — PageImage replay によるコミット済みデータの完全復元
- **B+Tree インデックス** — 完全一致・範囲検索（`SeekIndex` / `RangeIndex` 公開 API）
- **BulkLoader** — append-only バルクロードで通常 TX 比数倍のスループット
- **AdjacencyBlockStore** — ページ連続配置による高速隣接リスト（低次数・高次数を統一ストレージで管理）
- **クエリ最適化** — ヒストグラム統計 + ルールベース Optimizer でスキャン順序を自動選択
- **Streaming cursor** — `AsCursor()` / `AsEnumerable()` で大量結果をメモリを抑えて逐次処理
- **グラフアルゴリズム** — 重み付き最短経路（Dijkstra / A*）・BFS・可変長トラバーサル・パターンマッチ

## クイックスタート

### ローレベル API（低レイヤー直接操作）

```csharp
using var db = GraphDatabase.Open("./mygraph");

using (var tx = db.BeginTransaction())
{
    var alice = tx.CreateNode("Person");
    tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
    tx.SetProperty(alice, "age",  PropertyValue.FromInt32(30));

    var bob = tx.CreateNode("Person");
    tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));

    tx.CreateRelationship(alice, bob, "KNOWS");
    tx.Commit();
}
```

### Source Generator（型安全な CRUD）

#### 属性リファレンス

| 属性 | 対象 | 引数 | 省略時の挙動 |
|---|---|---|---|
| `[Node]` | クラス | `label` (省略可) | クラス名をラベルとして使用 |
| `[Relationship]` | クラス | `type` (省略可) | クラス名をリレーションシップ型として使用 |
| `[Property]` | プロパティ | `key` (省略可) | プロパティ名をグラフキーとして使用 |
| `[Indexed]` | プロパティ | `indexName` (省略可) | `idx_{label}_{propertyName}` を自動生成。`[Property]` と併用必須 |

> **注意:** クラス名・プロパティ名を変更すると `[Node]`・`[Indexed]` の自動生成名も変わり、既存インデックスファイルが孤立します。名前が変わる可能性がある場合は明示指定を推奨します。
>
> **名前衝突について:** 属性はすべて `Quiver.Api` 名前空間にあります。`[Node]` / `[Property]` のような一般名は他ライブラリの属性 (例: FsCheck の `[Property]`) と衝突し得ます。その場合は名前空間で限定してください (例: `[Quiver.Api.Property]`、または相手側を `[FsCheck.Xunit.Property]`)。これは名前空間で解決する設計です。

#### モデル定義例

```csharp
// ラベル・インデックス名はすべて省略可能（クラス名・プロパティ名から自動生成）
[Node]               // label = "Person"
public partial class Person
{
    [Indexed]        // indexName = "idx_person_name"
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

// 明示指定も可（リネーム耐性が必要な場合）
[Node("Person")]
public partial class Person
{
    [Indexed("idx_person_name")]
    [Property]
    public string Name { get; set; } = "";
}
```

SourceGenerator は各クラスに対して以下のメソッドを生成します。

**`[Node]` クラス**

| メソッド | シグネチャ | 説明 |
|---|---|---|
| `Insert` | `(tx, entity) → NodeId` | ノードを作成してプロパティを保存 |
| `InsertIndexed` | `(tx, entity) → NodeId` | `Insert` + `[Indexed]` プロパティをインデックス登録 |
| `Load` | `(tx, id) → T` | プロパティを読み込んでインスタンスを復元 |
| `Update` | `(tx, id, entity)` | 既存ノードのプロパティを上書き |
| `Delete` | `(tx, id)` | ノードを削除 |
| `FindBy{PropName}` | `(tx, value) → List<(NodeId, T)>` | `[Indexed]` プロパティごとに生成 |

**`[Relationship]` クラス**

| メソッド | シグネチャ | 説明 |
|---|---|---|
| `Insert` | `(tx, from, to, entity) → RelationshipId` | リレーションシップを作成してプロパティを保存 |
| `Load` | `(tx, id) → T` | プロパティを読み込んでインスタンスを復元 |
| `Update` | `(tx, id, entity)` | 既存リレーションシップのプロパティを上書き |
| `Delete` | `(tx, id)` | リレーションシップを削除 |

#### CRUD 使用例

```csharp
using var db = GraphDatabase.Open("./mygraph");
using var tx = db.BeginTransaction();

var aliceId = Person.InsertIndexed(tx, new Person { Name = "Alice", Age = 30 });
var alice   = Person.Load(tx, aliceId);

// インデックス検索（生成された FindBy* メソッド）
var results = Person.FindByName(tx, "Alice");

Person.Update(tx, aliceId, alice with { Age = 31 });
tx.Commit();
```

#### リレーションシップの操作

`[Relationship<TSource, TTarget>]` 属性でリレーションシップモデルを定義すると、Source Generator が CRUD メソッドと、始点 `TSource` から終点 `TTarget` への**型保存トラバーサル糖衣**（リレーション型名そのもののメソッド）を生成します。型名を省略するとクラス名がリレーション型になります。

```csharp
[Relationship<Person, Person>("KNOWS")]
public partial class Knows
{
    [Property]
    public string Since { get; set; } = "";
}

// 型保存トラバーサル: Knows() が TypedGraphTraversal<Person> を保つ (ホップ間で型が降格しない)
var known = g.Nodes<Person>()
             .Has(p => p.Name, "Alice")
             .Knows()                       // Person -KNOWS-> Person
             .Has(p => p.Age, P.Lt(30L))
             .ToList();
```

```csharp
// ── Source Generator API ────────────────────────────────
using (var tx = db.BeginTransaction())
{
    var aliceId = Person.InsertIndexed(tx, new Person { Name = "Alice", Age = 30 });
    var bobId   = Person.InsertIndexed(tx, new Person { Name = "Bob",   Age = 25 });

    var relId = Knows.Insert(tx, aliceId, bobId, new Knows { Since = "2024-01" });
    var rel   = Knows.Load(tx, relId);
    tx.Commit();
}

// ── 低レベル API ────────────────────────────────────────
using (var tx = db.BeginTransaction())
{
    var aliceId = Person.InsertIndexed(tx, new Person { Name = "Alice", Age = 30 });
    var bobId   = Person.InsertIndexed(tx, new Person { Name = "Bob",   Age = 25 });

    // プロパティなしの場合は低レベル API も利用可
    tx.CreateRelationship(aliceId, bobId, "KNOWS");
    tx.Commit();
}

// ── Gremlin ライク API ──────────────────────────────────
using (var tx = db.BeginTransaction())
{
    var g = tx.G(db.Schema);

    var alice = g.AddNode("Person").P("Name", "Alice").P("Age", 30).Next();
    var bob   = g.AddNode("Person").P("Name", "Bob").P("Age", 25).Next();

    g.AddRelationship("KNOWS").From(alice).To(bob).Next();

    // 隣接ノードのトラバーサル
    var friends = g.Nodes().HasLabel("Person")
                    .Has("Name", P.Eq("Alice"))
                    .Out("KNOWS")
                    .Values("Name")
                    .ToList();   // → ["Bob"]

    tx.Commit();
}
```

### Gremlin ライク API（グラフトラバーサル）

```csharp
var g = tx.G(db.Schema);

// 書き込み
var alice = g.AddNode("Person").P("Name", "Alice").P("Age", 30).Next();
var bob   = g.AddNode("Person").P("Name", "Bob").P("Age", 25).Next();
g.AddRelationship("KNOWS").From(alice).To(bob).Next();

// 型なしトラバーサル
var names = g.Nodes().HasLabel("Person")
              .Has("Age", P.Gt(25L))
              .Values("Name")
              .ToList();

// 型付きトラバーサル（式ツリーでプロパティ参照）
var people = g.Nodes<Person>()
              .Has(p => p.Age, P.Gt(25L))
              .ToList();  // → List<Person>（自動ロード）

// GC-7: LINQ ライクな式ツリー述語（比較 / && / 同一キー || / StartsWith 等）
var adults = g.Nodes<Person>()
              .Where(p => p.Age > 25 && p.Name.StartsWith("A"))
              .ToList();
// FT-35: 浮動小数点 (double/float/Half) の範囲比較も可（float は Double に widen 格納）
var tall = g.Nodes<Person>().Where(p => p.Height > 1.7f).ToList();
// P.Gt(double) 等の述語直指定も可: .Has("score", P.Between(1.0, 2.0))

// GC-8: エッジ（リレーションシップ）プロパティでの絞り込み
//   生成糖衣にエッジ述語を渡すと、型保存したまま絞り込んで終点へ辿る
var recent = g.Nodes<Person>()
              .Where(p => p.Name == "Alice")
              .Knows(e => e.Since == "2024-01")  // Knows エッジの Since で絞り込み
              .ToList();
// 型なしエッジ経路でも可: g.Node(a).OutRelationships("KNOWS").Has("since", P.Gt(2022L)).TargetNode()

// グラフパターンマッチ（Match DSL）
var results = g.Match(
    GraphPattern.Node("n", "Person")
                .Out("KNOWS", GraphPattern.Node("m", "Person"))
)
.Where("n", "Age", P.Gt(25L))
.Return(v => new
{
    PersonName = v["n"].Get<string>("Name"),
    FriendName = v["m"].Get<string>("Name"),
})
.ToList();

// サブトラバーサル述語（WHERE EXISTS / NOT EXISTS 相当）
var loners = g.Nodes().HasLabel("Person")
               .Not(t => t.Out("KNOWS"))          // KNOWS エッジを持たないノード
               .ToList();

var connectors = g.Nodes().HasLabel("Person")
                   .Where(t => t.Out("KNOWS").HasLabel("Person"))
                   .ToList();

// ストリーミング（大量結果でメモリを抑えたい場合）
using var cursor = g.Nodes<Person>().AsCursor();
while (cursor.MoveNext())
{
    var person = cursor.Current;   // トランザクション有効期間内のみ有効
}

// AsEnumerable で foreach / LINQ
foreach (var name in g.Nodes().HasLabel("Person").Values("Name").AsEnumerable())
    Console.WriteLine(name);
```

## アーキテクチャ

```
Quiver.Client           ← Gremlin ライク API / Match DSL / SourceGen 糖衣構文
├── Quiver.Client.Attributes  ← [Node] / [Property] / [Indexed]
└── Quiver.SourceGen   ← Roslyn IIncrementalGenerator (CRUD + FindBy* 生成)

Quiver              ← 公開 API ファサード
├── Quiver.Operators    ← Volcano 型物理演算子
├── Quiver.Transactions ← TransactionManager / LockManager / RecoveryManager
├── Quiver.Wal          ← Write-Ahead Log (グループコミット)
├── Quiver.Index        ← B+Tree インデックス
├── Quiver.Stores       ← Node / Relationship / Property / Token ストア
├── Quiver.Codec        ← Span<byte> シリアライザ
├── Quiver.Storage      ← ページ管理 + バッファプール (8KB ページ)
└── Quiver.Core         ← 共通型・例外・抽象インタフェース
```

### 依存関係

```
Core ← Storage ← Codec ← Stores ─┬─ Index
                                   │
                     Wal ──────────┤
                                   ▼
                         Transactions → Operators → Engine(Facade)
                                                        ↑
                                              Engine.Client(.Attributes)
                                              Engine.Client.SourceGen (Analyzer)
```

### ストレージ仕様

| 項目 | 値 |
|---|---|
| ページサイズ | 8 KB |
| エンディアン | Little-Endian |
| バッファプール | デフォルト 256 MB |
| WAL セグメント | デフォルト 64 MB |
| 文字列エンコーディング | UTF-8 (長さプレフィックス付き) |

### ID 型

すべての識別子は `readonly record struct` で型安全に表現する。

```csharp
public readonly record struct NodeId(long Value);
public readonly record struct RelationshipId(long Value);
public readonly record struct PropertyId(long Value);
public readonly record struct LabelId(int Value);
public readonly record struct TransactionId(long Value);
// ... など
```

`-1` は「無効 / null」を意味する予約値。

## ビルド

```bash
dotnet build Quiver.slnx
dotnet test Quiver.slnx
dotnet run --project sandbox/QuiverSandbox
```

要件: .NET 10 以上 / C# 13 以上

## テスト

| カテゴリ | フレームワーク | 配置 |
|---|---|---|
| ユニットテスト | xUnit + FluentAssertions | `tests/*.UnitTests/` |
| 結合テスト | xUnit + 一時ディレクトリ | `tests/*.IntegrationTests/` |
| 性能テスト | BenchmarkDotNet | `tests/*.StressTests/` |
| プロパティテスト | FsCheck.Xunit | ユニットテスト内 |

コア層のカバレッジ目標: **分岐カバレッジ 80% 以上**

## 性能目標と実測

「設計目標」は初期設計時の目標値。「実測」は現行ビルドの参考計測値で、
standalone runner `--basic-perf`（[BasicPerfRunner.cs](benchmarks/Quiver.Benchmarks/Standalone/BasicPerfRunner.cs)、
`dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --basic-perf`）を
AMD Ryzen 7 5700X / .NET 10 / best-of-N の in-process Stopwatch で計測した値（2026-06-09）。
厳密な再現は `benchmarks/Quiver.Benchmarks` の BenchmarkDotNet ベンチで。

| 操作 | 設計目標 | 実測 (2026-06-09) |
|---|---|---|
| ノード作成（単一 tx 償却） | < 1 µs ※インメモリ操作目標 | **~14 µs/op**（~70K ops/s） |
| ノード作成 + プロパティ設定（同上） | < 2 µs | **~22 µs/op** |
| リレーション作成（同上） | — | **~30 µs/op**（~34K ops/s） |
| 単発 durable commit（1 op = 1 commit、単一スレッド） | — | **~1.0 ms/commit**（WAL flush 律速） |
| 1-hop scan（degree 100、AdjacencyBlockStore） | < 0.5 µs | **~0.35 µs**（~3.5 ns/edge） |
| 1-hop scan（degree 100、linked-list / 索引なし） | — | **~11 µs**（~0.11 µs/edge、MVCC 可視性込み） |
| BFS 2-hop（ハブ degree 100、leaf 10,000、隣接ブロック） | < 5 ms | **~0.037 ms** |
| 1-hop クエリ（`g.Node().Out()`、degree 100、隣接ブロック） | クエリラッパ < 5% | **~4.2 µs/query**（~42 ns/edge、生隣接の ~12×） |
| BulkLoader（10 万 edge） | 通常 TX 比 5× 以上高速 | 通常 TX（batch 1000）比 **~11.8×** |

> **読み取りは隣接インデックスの有無で 30× 以上変わる。** `BeginBulkLoad(buildAdjacencyIndex: true)`
> で隣接ブロックを構築すると 1-hop が ~3.5 ns/edge になり、索引なしの linked-list 経路
> （~0.11 µs/edge、MVCC 可視性チェック込み）より degree 100 で **~31×** 速い。読み取り主体の
> ワークロードでは隣接インデックスを構築すること。
>
> **バッファプールの checksum 検証は disk→frame ロード時のみ行う（pin ごとには再計算しない）。**
> 常駐フレームの内容は disk 破損に晒されず、書込は `UnpinDirty` で CRC を更新し `FrameLock` が
> read/write pin を排他するため、pin ごとの全 8KB CRC32 は冗長だった。これを撤去し torn write / bit rot の
> ロード時検出は維持（crash contract / chaos テストで担保）。この 1 点で linked-list 1-hop が
> **~2.3 → ~0.11 µs/edge（~20×）**、隣接ブロック・2-hop・書き込みも軒並み高速化した。
>
> **書き込みは単発 durable commit が ~1 ms（WAL flush 律速）。** 大量書き込みは 1 tx にまとめる
> （償却 ~17 µs/node）か BulkLoader を使う。並行 commit では group commit
> （`GraphDatabaseOptions.GroupCommitWindow`）でスループットが桁違いに上がる
> （64-thread で window=0 比 ~28×、別計測 FT-27）。
>
> **クエリ DSL（`g.Node().Out()` 等）の 1-hop（degree 100）は ~4.2 µs/query（~42 ns/edge、生の隣接
> アクセスの ~12×）。** プラン構築 + 物理オペレータ生成は ~0.4 µs と僅少。結果行ごとの NodeId 世代
> スタンプ（識別子の往復一貫性のための version 解決）は、スロット再利用（vacuum 回収）が無い間は
> version sidecar 読み取りを省く高速パスで処理する。これで 1-hop クエリは ~62 → ~8 µs/query（**~7.7×**）に
> 短縮し、さらに checksum-at-load（上記）で隣接ブロック pin が安くなり ~8 → ~4.2 µs/query になった。

**PW-18 (複雑/ネストクエリ regression sentinel)**: `HasLabel + Has + Out + Has + Where(sub) + Order + Limit` の 7 step チェーンが 10K Person / AvgDegree=8 で ~367 ms、`Union(3 branches)` は単一 Out の 2.7× (16 → 44 ms)、`As/Select<T>` carry-column は no-alias 比 ±3% 以内。詳細: `benchmarks/Quiver.Benchmarks/{FilterChainExpand,BranchedTraversal,AsSelectProjection,MergeWorkload,OptimizerPlanRegression}Benchmarks.cs`。

**重み付き最短経路 (Dijkstra / A*)**: 格子グラフ・隅から隅・各エッジ重み 1.0・property-chain 重み参照で計測 (AMD Ryzen 7 5700X / .NET 10)。`WeightedShortestPath` (Dijkstra) は BFS ホップ数最短の 1.8〜1.9×、`WeightedShortestPathAStar` (Euclidean ヒューリスティック) は Dijkstra より速い。20×20: BFS 1.05 ms / Dijkstra 1.99 ms / A* 1.80 ms、40×40: BFS 4.27 ms / Dijkstra 7.79 ms / A* 7.63 ms。詳細: `benchmarks/Quiver.Benchmarks/WeightedShortestPathBenchmarks.cs`。

## 開発状況

### アーキテクチャ Wave

| Wave | 内容 | 状態 |
|---|---|---|
| Wave 1 | Storage / Codec / WAL | 完了 |
| Wave 2 | Stores / Index | 完了 |
| Wave 3 | TransactionManager / LockManager / RecoveryManager | 完了 |
| Wave 4 | Physical Operators / Engine API Facade | 完了 |
| Wave 5 | Client Layer (Source Generator / Gremlin API / Match DSL) | 完了 |

### Feature Tasks

| タスク | 内容 | 状態 |
|---|---|---|
| FT-1 | Relationship プロパティ対応 | 完了 |
| FT-2 | `[Relationship]` 属性 | 完了 |
| FT-3 | Relationship Source Generator | 完了 |
| FT-4 | 型付き Traversal API（`Out<TRel>()` など） | 完了 |
| FT-6 | `QuiverDb.OpenDatabase` 入口 API | 完了 |
| FT-7 | SourceGen: `[Relationship("KNOWS")]` の type 値を正しく生成 | 完了 |
| FT-8 | `GraphTransaction.SeekIndex` / `RangeIndex` 公開 API | 完了 |
| FT-9 | WAL PageImage replay（クラッシュ後の完全なデータ復旧） | 完了 |
| FT-5 | Namespace 整理（`GraphDb.Engine.*` → `Quiver.*`） | 完了 |
| Phase2 | サブトラバーサル述語（`.Where()` / `.Not()`） | 完了 |

### Perf Wave

| Wave | 内容 | 状態 |
|---|---|---|
| PW-1 | BenchmarkDotNet 測定基盤 | 完了 |
| PW-2 | B+Tree バイナリサーチ + QueryCursor streaming | 完了 |
| PW-3 | BulkLoader（append-only バルクロード） | 完了 |
| PW-4 | AdjacencyBlockStore（ページ連続配置の隣接リスト） | 完了 |
| PW-5 | 多段 Operator（BfsOperator / ShortestPathOperator など） | 完了 |
| PW-6 | ヒストグラム統計 + ルールベース Optimizer | 完了 |
| PW-7 | 並列 BFS（ParallelBfsOperator） | 完了 |
| PW-11 | Client 層 streaming cursor（`AsCursor()` / `AsEnumerable()`） | 完了 |
| PW-8 | 高次数ノード向け BFS adjacency iterator | **未完** |
| PW-9 | BulkLoader streaming / chunk build（1000 万 edge 超対応） | **未完** |
| PW-10 | B+Tree 更新系 allocation 削減 | **未完** |

### Gremlin / Cypher Compat

対応状況の詳細は [docs/design/gremlin_cypher_compat.md](docs/design/gremlin_cypher_compat.md) を参照。
基本探索・比較述語・CRUD は全対応。集約・可変長パス・`as/select` 公開・パス操作は Phase 2 以降。

次のフェーズ候補: Gremlin compat API 拡充（GC-1〜4）、Cypher 文字列パーサ、NativeAOT 最終検証

## Documentation

`docfx` を用いた API リファレンス + 概念解説 + チュートリアルが [docs/api/](docs/api/) にまとまっている。ローカルビルドは:

```bash
dotnet tool install -g docfx
docfx build docfx.json
docfx serve docs/api/_site
```

主なエントリ:

- [Getting Started](docs/api/getting-started.md)
- [Concepts](docs/api/concepts/index.md) — Node/Relationship、Transaction、Traversal、MERGE、KNN、Backends
- [Tutorials](docs/api/tutorials/index.md)
- [Cookbook](docs/cookbook.md) — よく使う典型レシピ集
- [運用ガイド (Operations)](docs/operations/README.md) — quickstart / backup・restore / performance tuning / recovery / known limits

## Samples

[`samples/`](samples/) 配下に機能別の独立サンプルプロジェクトがある。`dotnet run --project samples/<name>` で完走する。

| サンプル | 内容 |
|---|---|
| [`Quiver.Samples.Crud`](samples/Quiver.Samples.Crud/) | 基本 CRUD (ノード / リレーション / プロパティの作成・更新・削除) |
| [`Quiver.Samples.Traversal`](samples/Quiver.Samples.Traversal/) | 多段トラバーサル、フィルタ、可変長 repeat、集約、サブトラバーサル述語、cursor |
| [`Quiver.Samples.Match`](samples/Quiver.Samples.Match/) | Match DSL によるパターンマッチと MERGE / UPSERT |
| [`Quiver.Samples.SourceGen`](samples/Quiver.Samples.SourceGen/) | `[Node]` / `[Relationship]` 属性ベースの型付き CRUD |
| [`Quiver.Samples.Vector`](samples/Quiver.Samples.Vector/) | VEC-5 KNN 起点トラバーサル + VEC-6 graph-first ハイブリッド |

## 設計ドキュメント

詳細な設計仕様は [docs/design/](docs/design/) を参照。

| ファイル | 内容 |
|---|---|
| [00_conventions.md](docs/design/00_conventions.md) | 共通規約 (命名・性能指針・テスト規約) |
| [01_storage_paging.md](docs/design/01_storage_paging.md) | ページ管理・バッファプール |
| [02_record_codec.md](docs/design/02_record_codec.md) | バイト列直接操作プリミティブ |
| [03_fixed_record_stores.md](docs/design/03_fixed_record_stores.md) | Node / Relationship ストア |
| [04_property_token_stores.md](docs/design/04_property_token_stores.md) | Property / Token ストア |
| [05_btree_index.md](docs/design/05_btree_index.md) | B+Tree インデックス |
| [06_wal.md](docs/design/06_wal.md) | Write-Ahead Log |
| [07_transaction_recovery.md](docs/design/07_transaction_recovery.md) | トランザクション・リカバリ |
| [08_physical_operators.md](docs/design/08_physical_operators.md) | Volcano 型物理演算子 |
| [09_graph_api.md](docs/design/09_graph_api.md) | 公開 CRUD API |

## Versioning

Quiver は [Semantic Versioning](https://semver.org/lang/ja/) (`MAJOR.MINOR.PATCH`) に従う。MAJOR は breaking change、MINOR は後方互換な機能追加、PATCH はバグ修正のみ。`1.0.0` 未満 (`0.x` / `-rc`) は安定性の保証対象外。

安定性を保証する public API は `Quiver` / `Quiver.Client` / `Quiver.Core` の public 型に限る。非推奨化は最低 1 MINOR の `[Obsolete]` 告知期間を置いてから次の MAJOR で削除する。

public API surface は [tests/Quiver.PublicApi.Tests/](tests/Quiver.PublicApi.Tests/) の approval test (`PublicApiGenerator`) で機械的に固定されており、意図しない breaking change は CI で検出される。

詳細は [docs/api-stability.md](docs/api-stability.md) を参照。

## ライセンス

(未定)

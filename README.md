# Quiver

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
| `[GraphNode]` | クラス | `label` (省略可) | クラス名をラベルとして使用 |
| `[GraphRelationship]` | クラス | `type` (省略可) | クラス名をリレーションシップ型として使用 |
| `[GraphProperty]` | プロパティ | `key` (省略可) | プロパティ名をグラフキーとして使用 |
| `[GraphIndexed]` | プロパティ | `indexName` (省略可) | `idx_{label}_{propertyName}` を自動生成。`[GraphProperty]` と併用必須 |

> **注意:** クラス名・プロパティ名を変更すると `[GraphNode]`・`[GraphIndexed]` の自動生成名も変わり、既存インデックスファイルが孤立します。名前が変わる可能性がある場合は明示指定を推奨します。

#### モデル定義例

```csharp
// ラベル・インデックス名はすべて省略可能（クラス名・プロパティ名から自動生成）
[GraphNode]               // label = "Person"
public partial class Person
{
    [GraphIndexed]        // indexName = "idx_person_name"
    [GraphProperty]
    public string Name { get; set; } = "";

    [GraphProperty]
    public int Age { get; set; }
}

// 明示指定も可（リネーム耐性が必要な場合）
[GraphNode("Person")]
public partial class Person
{
    [GraphIndexed("idx_person_name")]
    [GraphProperty]
    public string Name { get; set; } = "";
}
```

SourceGenerator は各クラスに対して以下のメソッドを生成します。

**`[GraphNode]` クラス**

| メソッド | シグネチャ | 説明 |
|---|---|---|
| `Insert` | `(tx, entity) → NodeId` | ノードを作成してプロパティを保存 |
| `InsertIndexed` | `(tx, entity) → NodeId` | `Insert` + `[GraphIndexed]` プロパティをインデックス登録 |
| `Load` | `(tx, id) → T` | プロパティを読み込んでインスタンスを復元 |
| `Update` | `(tx, id, entity)` | 既存ノードのプロパティを上書き |
| `Delete` | `(tx, id)` | ノードを削除 |
| `FindBy{PropName}` | `(tx, value) → List<(NodeId, T)>` | `[GraphIndexed]` プロパティごとに生成 |

**`[GraphRelationship]` クラス**

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

`[GraphRelationship]` 属性でリレーションシップモデルを定義すると、Source Generator が CRUD メソッドを生成します。型付きトラバーサル（`Out<TRel>()` など）にも対応します。

```csharp
[GraphRelationship("KNOWS")]
public partial class Knows
{
    [GraphProperty]
    public string Since { get; set; } = "";
}
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
    var friends = g.V().HasLabel("Person")
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
var names = g.V().HasLabel("Person")
              .Has("Age", P.Gt(25L))
              .Values("Name")
              .ToList();

// 型付きトラバーサル（式ツリーでプロパティ参照）
var people = g.V<Person>()
              .Has(p => p.Age, P.Gt(25L))
              .ToList();  // → List<Person>（自動ロード）

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
var loners = g.V().HasLabel("Person")
               .Not(t => t.Out("KNOWS"))          // KNOWS エッジを持たないノード
               .ToList();

var connectors = g.V().HasLabel("Person")
                   .Where(t => t.Out("KNOWS").HasLabel("Person"))
                   .ToList();

// ストリーミング（大量結果でメモリを抑えたい場合）
using var cursor = g.V<Person>().AsCursor();
while (cursor.MoveNext())
{
    var person = cursor.Current;   // トランザクション有効期間内のみ有効
}

// AsEnumerable で foreach / LINQ
foreach (var name in g.V().HasLabel("Person").Values("Name").AsEnumerable())
    Console.WriteLine(name);
```

## アーキテクチャ

```
Quiver.Client           ← Gremlin ライク API / Match DSL / SourceGen 糖衣構文
├── Quiver.Client.Attributes  ← [GraphNode] / [GraphProperty] / [GraphIndexed]
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

## 性能目標

以下はすべて設計目標値。実測値は `benchmarks/` の BenchmarkDotNet で計測可能。

| 操作 | 設計目標 |
|---|---|
| `CreateNode` | < 1 µs |
| `SetProperty` | < 2 µs |
| `EnumerateRelationships`（隣接 10 件） | < 1.5 µs |
| クエリエンジンのラッパオーバーヘッド | < 5% |
| BulkLoader（100 万 edge） | 通常 TX 比 5× 以上高速 |
| 1-hop scan（degree 100、AdjacencyBlockStore） | < 0.5 µs |
| BFS 2-hop（ハブ degree 100、2 段） | < 5 ms |

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
| FT-2 | `[GraphRelationship]` 属性 | 完了 |
| FT-3 | Relationship Source Generator | 完了 |
| FT-4 | 型付き Traversal API（`Out<TRel>()` など） | 完了 |
| FT-6 | `QuiverDb.OpenDatabase` 入口 API | 完了 |
| FT-7 | SourceGen: `[GraphRelationship("KNOWS")]` の type 値を正しく生成 | 完了 |
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

## ライセンス

(未定)

# Quiver

Pure C# で実装するグラフデータベースエンジン。Amazon Neptune や Apache TinkerPop のような本格的なグラフ DB のコア層を、マネージドコードのみで構築することを目標とする。

## 特徴

- **Pure C#** — アンマネージド依存なし。Windows / Linux / macOS (x64, ARM64) で動作
- **NativeAOT 対応** — 単一バイナリとして配布可能。リフレクション不使用
- **ゼロアロケーションホットパス** — `Span<T>` / `ref struct` でヒープ確保を排除
- **Volcano 型クエリエンジン** — 物理演算子を手書きで合成してクエリを実行
- **WAL + スナップショット分離** — クラッシュリカバリ付きトランザクション
- **B+Tree インデックス** — 完全一致・範囲検索に対応

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

```csharp
// モデル定義 — SourceGenerator が CRUD メソッドを自動生成
[GraphNode("Person")]
public partial class Person
{
    [GraphIndexed("idx_person_name")]
    [GraphProperty]
    public string Name { get; set; } = "";

    [GraphProperty]
    public int Age { get; set; }
}

// 使用例
using var db = GraphDatabase.Open("./mygraph");
using var tx = db.BeginTransaction();
var g = tx.G(db.Schema);

var id = g.InsertIndexed(new Person { Name = "Alice", Age = 30 });
var alice = g.Load<Person>(id);

// インデックス検索（生成された FindBy* メソッド）
var results = Person.FindByName(tx, "Alice");

tx.Commit();
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
```

## アーキテクチャ

```
GraphDb.Engine.Client           ← Gremlin ライク API / Match DSL / SourceGen 糖衣構文
├── GraphDb.Engine.Client.Attributes  ← [GraphNode] / [GraphProperty] / [GraphIndexed]
└── GraphDb.Engine.Client.SourceGen   ← Roslyn IIncrementalGenerator (CRUD + FindBy* 生成)

GraphDb.Engine              ← 公開 API ファサード
├── GraphDb.Engine.Operators    ← Volcano 型物理演算子
├── GraphDb.Engine.Transactions ← TransactionManager / LockManager / RecoveryManager
├── GraphDb.Engine.Wal          ← Write-Ahead Log (グループコミット)
├── GraphDb.Engine.Index        ← B+Tree インデックス
├── GraphDb.Engine.Stores       ← Node / Relationship / Property / Token ストア
├── GraphDb.Engine.Codec        ← Span<byte> シリアライザ
├── GraphDb.Engine.Storage      ← ページ管理 + バッファプール (8KB ページ)
└── GraphDb.Engine.Core         ← 共通型・例外・抽象インタフェース
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

| 操作 | 目標 |
|---|---|
| `CreateNode` | < 1 µs |
| `SetProperty` | < 2 µs |
| `EnumerateRelationships`(隣接 10 件) | < 1.5 µs |
| クエリエンジンのラッパオーバーヘッド | < 5% |

## 開発状況

| Wave | 内容 | 状態 |
|---|---|---|
| Wave 1 | Storage / Codec / WAL | 完了 |
| Wave 2 | Stores / Index | 完了 |
| Wave 3 | TransactionManager / LockManager / RecoveryManager | 完了 |
| Wave 4 | Physical Operators / Engine API Facade | 完了 |
| Wave 5 | Client Layer (Source Generator / Gremlin API / Match DSL) | 完了 |

次のフェーズ候補: Cypher 文字列パーサ、LINQ プロバイダ、NativeAOT 最終検証

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

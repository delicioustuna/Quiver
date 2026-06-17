# Quiver 開発者向けドキュメント

README はライブラリ利用者向けの最小限に絞っているため、内部アーキテクチャ・依存関係・
ストレージ仕様・ビルド/テスト手順・開発状況・バージョニング規約・詳細な性能計測は本ファイルに集約する。

- 設計仕様 (as-built): [docs/spec/](spec/)
- ロードマップ / タスク状況: [docs/roadmap.md](roadmap.md)
- API 安定性ポリシー: [docs/api-stability.md](api-stability.md)
- 運用ガイド: [docs/operations/README.md](operations/README.md)

## パッケージ構成

| パッケージ | 役割 |
|---|---|
| `Quiver` | エンジン中核 + 公開ファサード。型付き属性（`[Node]` / `[Relationship]` / `[Property]` / `[Indexed]`、namespace `Quiver.Api`）を本体に内包し、`Quiver.SourceGen` を analyzer として同梱。これ 1 つの参照で型安全 CRUD まで使える |
| `Quiver.SourceGen` | Roslyn `IIncrementalGenerator`（CRUD / `FindBy*` / 型保存トラバーサル糖衣を生成）。単体公開せず `Quiver` に同梱する内部プロジェクト |
| `Quiver.Embedding` | テキスト埋め込みパイプライン（VEC-4）。**incubating: NuGet 非公開**（`IsPackable=false`。「NuGet パッケージ化」§incubating 参照） |
| `Quiver.Rag` | ローカル RAG レイヤ（Document/Chunk スキーマ・取込・hybrid 検索 + graph expansion）。**開発中** ([design/14](design/14_rag_layer.md)) |
| `Quiver.Hosting` | `Microsoft.Extensions.Hosting` 連携（DI 登録） |
| `Quiver.OpenTelemetry` | OpenTelemetry エクスポート |

## アーキテクチャ

中核エンジンは単一アセンブリ `Quiver` に集約し、名前空間でレイヤを分離する。

```
Quiver.Api / Quiver.Api.Match  ← 公開ファサード: GraphDatabase / GraphTransaction / Fluent Traversal / Match DSL
Quiver.Query.Logical
Quiver.Query.Optimizer         ← ヒストグラム統計 + ルールベース最適化
Quiver.Query.Physical          ← Volcano 型物理演算子
Quiver.Transactions            ← TransactionManager / LockManager / RecoveryManager
Quiver.Storage.Wal             ← Write-Ahead Log（group commit）
Quiver.Index                   ← B+Tree インデックス
Quiver.Storage.Records         ← Node / Relationship / Property / Token ストア
Quiver.Codec                   ← Span<byte> シリアライザ
Quiver.Storage                 ← ページ管理 + バッファプール（8KB ページ）
Quiver.Core                    ← 共通型・例外・抽象インタフェース
```

### 依存関係（パッケージ）

```
Quiver.SourceGen ─(analyzer 同梱)─► Quiver ─┬─► Quiver.Embedding
                                            ├─► Quiver.Rag (開発中)
                                            ├─► Quiver.Hosting
                                            └─► Quiver.OpenTelemetry
```

### ストレージ仕様

| 項目 | 値 |
|---|---|
| ページサイズ | 8 KB |
| エンディアン | Little-Endian |
| バッファプール | デフォルト 256 MB |
| WAL セグメント | デフォルト 64 MB |
| 文字列エンコーディング | UTF-8（長さプレフィックス付き） |
| 静止時のファイル | `*.quiver` 単一ファイル |
| 運用中のファイル | `*.quiver` + `*.quiver-wal` |

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

`-1` は「無効 / null」を意味する予約値。`NodeId.Value` は generation + sequence をパックした値で、
スロット再利用後も識別子の往復一貫性を保つ。

## ビルド

```bash
dotnet build Quiver.slnx
dotnet test  Quiver.slnx
dotnet run --project sandbox/QuiverSandbox
```

要件: .NET 10 以上 / C# 13 以上。各タスクでは `dotnet build` でビルド成功を確認すること。

## テスト

| カテゴリ | フレームワーク | 配置 |
|---|---|---|
| ユニットテスト | xUnit + FluentAssertions | `tests/*.UnitTests/` |
| 結合テスト | xUnit + 一時ディレクトリ | `tests/*.IntegrationTests/` |
| 性能テスト | BenchmarkDotNet | `tests/*.StressTests/` / `benchmarks/` |
| プロパティテスト | FsCheck.Xunit | ユニットテスト内 |
| public API 固定 | `PublicApiGenerator` approval test | `tests/Quiver.PublicApi.Tests/` |

コア層のカバレッジ目標: 分岐カバレッジ 80% 以上。

## ローレベル / 型なし API

Fluent Traversal / Source Generator の下位には、`GraphTransaction` を直接操作するローレベル API がある。

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

型なしトラバーサル（`tx.G(db.Schema)` 起点。文字列キーで指定）:

```csharp
var g = tx.G(db.Schema);

var alice = g.AddNode("Person").P("Name", "Alice").P("Age", 30).Next();
var bob   = g.AddNode("Person").P("Name", "Bob").P("Age", 25).Next();
g.AddRelationship("KNOWS").From(alice).To(bob).Next();

var names = g.Nodes().HasLabel("Person")
              .Has("Age", P.Gt(25L))
              .Out("KNOWS")
              .Values("Name")
              .ToList();   // → ["Bob"]

// 型なしエッジ経路
var recent = g.Node(alice).OutRelationships("KNOWS").Has("since", P.Gt(2022L)).TargetNode();
```

グラフパターンマッチ（Match DSL, Cypher の宣言的パターンに相当）:

```csharp
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
var loners     = g.Nodes().HasLabel("Person").Not(t => t.Out("KNOWS")).ToList();
var connectors = g.Nodes().HasLabel("Person").Where(t => t.Out("KNOWS").HasLabel("Person")).ToList();

// ストリーミング（大量結果でメモリを抑える）
using var cursor = g.Nodes<Person>().AsCursor();
while (cursor.MoveNext())
{
    var person = cursor.Current;   // トランザクション有効期間内のみ有効
}
foreach (var name in g.Nodes().HasLabel("Person").Values("Name").AsEnumerable())
    Console.WriteLine(name);
```

### Source Generator 属性リファレンス

| 属性 | 対象 | 引数 | 省略時の挙動 |
|---|---|---|---|
| `[Node]` | クラス | `label` (省略可) | クラス名をラベルとして使用 |
| `[Relationship]` | クラス | `type` (省略可) | クラス名をリレーションシップ型として使用 |
| `[Property]` | プロパティ | `key` (省略可) | プロパティ名をグラフキーとして使用 |
| `[Indexed]` | プロパティ | `indexName` (省略可) | `idx_{label}_{propertyName}` を自動生成。`[Property]` と併用必須 |

> **注意:** クラス名・プロパティ名を変更すると `[Node]`・`[Indexed]` の自動生成名も変わり、既存
> インデックスファイルが孤立する。リネームの可能性がある場合は明示指定を推奨。
>
> **名前衝突:** 属性はすべて `Quiver.Api` 名前空間にある。`[Node]` / `[Property]` のような一般名は
> 他ライブラリの属性（例: FsCheck の `[Property]`）と衝突し得る。その場合は名前空間で限定する
> （例: `[Quiver.Api.Property]`）。

生成されるメソッド:

**`[Node]` クラス** — `Insert(tx, entity) → NodeId` / `InsertIndexed` / `Load(tx, id) → T` /
`Update(tx, id, entity)` / `Delete(tx, id)` / `FindBy{PropName}(tx, value) → List<(NodeId, T)>`（`[Indexed]` ごと）

**`[Relationship]` クラス** — `Insert(tx, from, to, entity) → RelationshipId` / `Load` / `Update` / `Delete`

## 性能（詳細計測）

`benchmarks/Quiver.Benchmarks` の BenchmarkDotNet ベンチ、および standalone runner
`--basic-perf`（[BasicPerfRunner.cs](../benchmarks/Quiver.Benchmarks/Standalone/BasicPerfRunner.cs)、
`dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --basic-perf`）で計測。
基本計測値は AMD Ryzen 7 5700X / .NET 10 / best-of-N の in-process Stopwatch（2026-06-09）。

| 操作 | 設計目標 | 実測 (2026-06-09) |
|---|---|---|
| ノード作成（単一 tx 償却） | < 1 µs ※インメモリ操作目標 | ~3.5–4 µs/op（~250K ops/s） |
| ノード作成 + プロパティ設定（同上） | < 2 µs | ~6 µs/op |
| リレーション作成（同上） | — | ~7 µs/op（~140K ops/s） |
| 単発 durable commit（1 op = 1 commit、単一スレッド） | — | ~1.0 ms/commit（WAL flush 律速） |
| 1-hop scan（degree 100、AdjacencyBlockStore） | < 0.5 µs | ~0.35 µs（~3.5 ns/edge） |
| 1-hop scan（degree 100、linked-list / 索引なし） | — | ~11 µs（~0.11 µs/edge、MVCC 可視性込み） |
| BFS 2-hop（ハブ degree 100、leaf 10,000、隣接ブロック） | < 5 ms | ~0.037 ms |
| 1-hop クエリ（`g.Node().Out()`、degree 100、隣接ブロック） | クエリラッパ < 5% | ~4.2 µs/query（~42 ns/edge、生隣接の ~12×） |
| BulkLoader（10 万 edge） | 通常 TX 比 5× 以上高速 | 通常 TX（batch 1000）比 ~11.8× |

### 計測の要点

- **読み取りは隣接インデックスの有無で 30× 以上変わる。** `BeginBulkLoad(buildAdjacencyIndex: true)`
  で隣接ブロックを構築すると 1-hop が ~3.5 ns/edge になり、索引なしの linked-list 経路
  （~0.11 µs/edge、MVCC 可視性チェック込み）より degree 100 で ~31× 速い。読み取り主体の
  ワークロードでは隣接インデックスを構築すること。
- **バッファプールの checksum 検証は disk→frame ロード時のみ行う（pin ごとには再計算しない）。**
  常駐フレームの内容は disk 破損に晒されず、書込は `UnpinDirty` で CRC を更新し `FrameLock` が
  read/write pin を排他するため、pin ごとの全 8KB CRC32 は冗長だった。撤去後も torn write / bit rot の
  ロード時検出は維持（crash contract / chaos テストで担保）。この 1 点で linked-list 1-hop が
  ~2.3 → ~0.11 µs/edge（~20×）短縮した。
- **書き込みは単発 durable commit が ~1 ms（WAL flush 律速）。** 大量書き込みは 1 tx にまとめる
  （償却 ~3.5–4 µs/node）か BulkLoader を使う。並行 commit では group commit
  （`GraphDatabaseOptions.GroupCommitWindow`）でスループットが桁違いに上がる
  （64-thread で window=0 比 ~28×、別計測 FT-27）。
- **WAL page-image の Encode（trim+RLE）は commit 時にページ毎 1 回だけ行う（書込ごとには行わない）。**
  トランザクション内で同一ページを繰り返し書いても WAL に出るのは最終状態 1 件（latest-wins coalesce）
  なので、中間状態の Encode は無駄。これを `FlushPending`（commit）へ遅延し、ホットページ反復書込
  （version sidecar / record heap）の増幅を解消。recovery 形式は不変で、単一 tx 償却の書込が ~4×
  高速化した（ノード作成 ~14 → ~3.5–4 µs/op）。
- **クエリ DSL の 1-hop（degree 100）は ~4.2 µs/query（~42 ns/edge、生隣接の ~12×）。**
  プラン構築 + 物理オペレータ生成は ~0.4 µs と僅少。結果行ごとの NodeId 世代スタンプは、スロット
  再利用（vacuum 回収）が無い間は version sidecar 読み取りを省く高速パスで処理する。これで 1-hop
  クエリは ~62 → ~8 µs/query（~7.7×）に短縮し、さらに checksum-at-load で隣接ブロック pin が安くなり
  ~8 → ~4.2 µs/query になった。

### PW-18（複雑/ネストクエリ regression sentinel）

`HasLabel + Has + Out + Has + Where(sub) + Order + Limit` の 7 step チェーンが 10K Person /
AvgDegree=8 で ~367 ms、`Union(3 branches)` は単一 Out の 2.7×（16 → 44 ms）、`As/Select<T>`
carry-column は no-alias 比 ±3% 以内。詳細:
`benchmarks/Quiver.Benchmarks/{FilterChainExpand,BranchedTraversal,AsSelectProjection,MergeWorkload,OptimizerPlanRegression}Benchmarks.cs`。

### 重み付き最短経路（Dijkstra / A*）

格子グラフ・隅から隅・各エッジ重み 1.0・property-chain 重み参照で計測（AMD Ryzen 7 5700X / .NET 10）。
`WeightedShortestPath`（Dijkstra）は BFS ホップ数最短の 1.8〜1.9×、`WeightedShortestPathAStar`
（Euclidean ヒューリスティック）は Dijkstra より速い。20×20: BFS 1.05 ms / Dijkstra 1.99 ms / A* 1.80 ms、
40×40: BFS 4.27 ms / Dijkstra 7.79 ms / A* 7.63 ms。詳細:
`benchmarks/Quiver.Benchmarks/WeightedShortestPathBenchmarks.cs`。

## 開発状況

完了済みマイルストーンと進行中タスクの一覧は [docs/roadmap.md](roadmap.md) を正本とする。
概略: Wave 1–5（Storage / Codec / WAL → Stores / Index → Transactions → Operators / Engine → Client 層）
完了。Feature（FT）・Perf（PW）・Gremlin/Cypher Compat（GC）・Backend Abstraction（BA）・
Vector/Embedding（VEC）の各系列が進行中。残タスク（PW-8/9/10 ほか）も roadmap 参照。

Gremlin / Cypher 互換の対応状況は [docs/spec/05_query.md](spec/05_query.md)。
基本探索・比較述語・CRUD・集約・可変長パス・`as/select` は対応済み。

## 設計ドキュメント

| ファイル | 内容 |
|---|---|
| [00_conventions.md](design/00_conventions.md) | 共通規約（命名・性能指針・テスト規約） |
| [01_storage_paging.md](design/01_storage_paging.md) | ページ管理・バッファプール |
| [02_record_codec.md](design/02_record_codec.md) | バイト列直接操作プリミティブ |
| [03_fixed_record_stores.md](design/03_fixed_record_stores.md) | Node / Relationship ストア |
| [04_property_token_stores.md](design/04_property_token_stores.md) | Property / Token ストア |
| [05_btree_index.md](design/05_btree_index.md) | B+Tree インデックス |
| [06_wal.md](design/06_wal.md) | Write-Ahead Log |
| [07_transaction_recovery.md](design/07_transaction_recovery.md) | トランザクション・リカバリ |
| [08_physical_operators.md](design/08_physical_operators.md) | Volcano 型物理演算子 |
| [09_graph_api.md](design/09_graph_api.md) | 公開 CRUD API |
| [10_embedding_pipeline.md](design/10_embedding_pipeline.md) | 埋め込み / ベクトル検索パイプライン |
| [11_rearchitecture_master_plan.md](design/11_rearchitecture_master_plan.md) | 抜本再設計マスタープラン |
| [12_rag_backend_direction.md](design/12_rag_backend_direction.md) | ローカル RAG バックエンド方向性（ポジショニング・非目標の正本） |
| [13_fulltext_search.md](design/13_fulltext_search.md) | 全文検索 / ハイブリッド検索（転置インデックス + BM25 + RRF） |
| [14_rag_layer.md](design/14_rag_layer.md) | Quiver.Rag レイヤ（Document/Chunk スキーマ・取込・検索） |

## Versioning / API 安定性

Quiver は [Semantic Versioning](https://semver.org/lang/ja/)（`MAJOR.MINOR.PATCH`）に従う。
MAJOR は breaking change、MINOR は後方互換な機能追加、PATCH はバグ修正のみ。`1.0.0` 未満（`0.x`）は
安定性の保証対象外。バージョンの正本は [Directory.Build.props](../Directory.Build.props) の `VersionPrefix`。

安定性を保証する public API は `Quiver` / `Quiver.Api` / `Quiver.Core` の public 型に限る。非推奨化は
最低 1 MINOR の `[Obsolete]` 告知期間を置いてから次の MAJOR で削除する。

public API surface は [tests/Quiver.PublicApi.Tests/](../tests/Quiver.PublicApi.Tests/) の approval test
（`PublicApiGenerator`）で機械的に固定されており、意図しない breaking change は CI で検出される。
詳細は [docs/api-stability.md](api-stability.md) を参照。

## NuGet パッケージ化

### 公開パッケージ

`dotnet pack Quiver.slnx` で以下 4 つのライブラリが NuGet パッケージ (`.nupkg` + symbol `.snupkg`) になる。
テスト / ベンチ / サンプル / sandbox は `IsPackable=false`（[Directory.Build.props](../Directory.Build.props) の既定）で除外される。

| パッケージ | 内容 | 依存 |
|---|---|---|
| `Quiver` | コアエンジン（型付き属性は本体に内包 + Source Generator を**同梱**） | System.IO.Hashing, Microsoft.Extensions.Logging.Abstractions |
| `Quiver.Hosting` | `Microsoft.Extensions.Hosting` / DI 統合 | `Quiver`, Microsoft.Extensions.* |
| `Quiver.OpenTelemetry` | OpenTelemetry 計装登録 | `Quiver`, OpenTelemetry(.Api) |
| `Quiver.Rag` | ローカル RAG スキーマ層 | `Quiver` |

#### incubating（非公開）

`Quiver.Embedding`（テキスト埋め込みパイプライン、VEC-4）は**現状 NuGet 公開しない**（`IsPackable=false`、
リポジトリには残しビルド/テスト対象）。理由: ①具体プロバイダ未同梱で単体では動かない（`IEmbeddingProvider` を
利用者が実装する必要がある）②実消費者が単体テストのみ ③主用途の `Quiver.Rag` はチャンク埋め込みを
`IChunkEmbedder` のインライン注入で行い本パイプラインを使わない。参照プロバイダ実装・サンプル・実消費者
（例: RAG の遅延/バックグラウンド埋め込みモード）が揃った時点で `IsPackable=true` にして公開へ昇格する。

型付きエンティティ属性（`[Node]` / `[Relationship]` / `[Property]` / `[Indexed]`、namespace `Quiver.Api`）は
**`Quiver` 本体アセンブリに内包**している（[src/Quiver/Client/NodeAttribute.cs](../src/Quiver/Client/NodeAttribute.cs)・
[RelationshipAttribute.cs](../src/Quiver/Client/RelationshipAttribute.cs)）。`Quiver.SourceGen`（Roslyn generator）は
**単体公開せず** `Quiver` パッケージへ analyzer として同梱する（`analyzers/dotnet/cs/Quiver.SourceGen.dll`、
[src/Quiver/Quiver.csproj](../src/Quiver/Quiver.csproj) の `_QuiverAddBundledAnalyzer` target）。生成器は属性を
**完全修飾名の文字列**で照合する（`GraphNodeGenerator.NodeAttributeFqn = "Quiver.Api.NodeAttribute"` 等）ため、
属性アセンブリへの参照は不要。SourceGen の `ProjectReference` は `PrivateAssets="all"` でパッケージ依存に昇格させない。

結果、利用者は `Quiver` パッケージ 1 つの参照で属性 + 生成器まで揃う。さらに `Quiver` は
[build/Quiver.props](../src/Quiver/build/Quiver.props) を `build/`・`buildTransitive/` に同梱し、`ImplicitUsings`
有効なプロジェクトには `Quiver` / `Quiver.Api` の global using を自動注入する（`using` 文ゼロのドロップイン。
不要なら利用者側で `<Using Remove="Quiver.Api" />` で opt-out 可）。

> リポジトリ内のテスト/サンプルは `Quiver` を `ProjectReference` するが、analyzer は `PrivateAssets="all"` で
> transitive には流れない。そのため `[Node]` 等を使うプロジェクトは `Quiver.SourceGen` を analyzer として
> 直接参照する（`Quiver.Tests` / `Quiver.Client.Tests` / `Samples.SourceGen` / `QuiverSandbox` / `SourceGen.Tests`）。
> 属性型は `Quiver` 本体から供給されるので、属性アセンブリの直接参照は不要。

### 共通メタデータ / 設定

パッケージ共通のメタデータ（Authors / ライセンス `MIT` / `RepositoryUrl` / `PackageReadmeFile` /
`PackageIcon` 等）は [Directory.Build.props](../Directory.Build.props) に一元化。README（リポジトリルートの
[README.md](../README.md)）とアイコン（`icon.png`）の同梱は [Directory.Build.targets](../Directory.Build.targets) で
`IsPackable=true` のプロジェクトにだけ取り込む（props は csproj 本文より前に評価され `IsPackable` が
未確定なため、targets 側で行う）。Source Link / 決定論ビルド / symbol package (`snupkg`) も props で有効。

### バージョン指定

バージョンの正本は `Directory.Build.props` の `VersionPrefix`（現在 `0.1.0`）。pre-release は
`-p:VersionSuffix=rc.1`（→ `0.1.0-rc.1`）で付与する。リリース時は git タグから明示指定もできる
（`dotnet pack -p:Version=0.1.0`）。`EnablePackageValidation` で pack 時に public API 差分を検証する
（baseline は最初の GA `1.0.0` 公開後に `PackageValidationBaselineVersion` で設定）。

### ローカルでの pack と公開

```bash
# 全パッケージを artifacts/nupkg に出力
dotnet pack Quiver.slnx -c Release -o artifacts/nupkg

# 中身確認 (例)
#   lib/net10.0/Quiver.dll (属性込み)
#   analyzers/dotnet/cs/Quiver.SourceGen.dll
#   build/Quiver.props, buildTransitive/Quiver.props (global using)
#   README.md, icon.png

# nuget.org へ公開 (API キーが必要。*.nupkg を push すると *.snupkg も自動送出)
dotnet nuget push "artifacts/nupkg/*.nupkg" --api-key <KEY> --source https://api.nuget.org/v3/index.json --skip-duplicate
```

CI では [.github/workflows/release.yml](../.github/workflows/release.yml) が `v*` タグ push を契機に
pack → `nuget.org` へ push する（API キーは GitHub secret `NUGET_API_KEY`）。手動実行
（`workflow_dispatch`）では artifact 生成のみ。

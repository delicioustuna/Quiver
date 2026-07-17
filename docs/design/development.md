# Quiver 開発者向けドキュメント

README はライブラリ利用者向けの最小限に絞っているため、内部アーキテクチャ・依存関係・
ストレージ仕様・ビルド/テスト手順・開発状況・バージョニング規約・詳細な性能計測は本ファイルに集約する。

- 設計仕様 (as-built): [docs/spec/](../spec/)
- 現行再設計の設計正本: [Single Writer + Snapshot Readers 抜本再設計](../../plans/single-writer-redesign.md)
- API 安定性ポリシー: [docs/api-stability.md](../api-stability.md)
- 運用ガイド: [docs/operations/README.md](../operations/README.md)

## パッケージ構成

| パッケージ | 役割 |
|---|---|
| `Quiver` | エンジン中核 + 公開ファサード。型付き属性（`[Vertex]` / `[Edge]` / `[Property]` / `[Indexed]`、namespace `Quiver.Api`）を本体に内包し、`Quiver.SourceGen` を analyzer として同梱。これ 1 つの参照で型安全 CRUD まで使える |
| `Quiver.SourceGen` | Roslyn `IIncrementalGenerator`（CRUD / `FindBy*` / 型保存トラバーサル糖衣を生成）。単体公開せず `Quiver` に同梱する内部プロジェクト |
| `Quiver.Embedding` | テキスト埋め込みパイプライン（VEC-4）。**incubating: NuGet 非公開**（`IsPackable=false`。「NuGet パッケージ化」§incubating 参照） |
| `Quiver.Rag` | ローカル RAG レイヤ（Document/Chunk スキーマ・取込・hybrid 検索 + graph expansion）。**開発中**（過去の設計ノートは historical record。現行の実装順序は再設計計画に従う） |
| `Quiver.Hosting` | `Microsoft.Extensions.Hosting` 連携（DI 登録） |
| `Quiver.OpenTelemetry` | OpenTelemetry エクスポート |

## アーキテクチャ

中核エンジンは単一アセンブリ `Quiver` に集約し、名前空間でレイヤを分離する。

```
Quiver.Api / Quiver.Api.Match  ← 公開ファサード: QuiverDatabase / GraphTransaction / Fluent Traversal / Match DSL
Quiver.Query.Logical
Quiver.Query.Optimizer         ← ヒストグラム統計 + ルールベース最適化
Quiver.Query.Physical          ← Volcano 型物理演算子
Quiver.Transactions            ← TransactionManager / LockManager / RecoveryManager
Quiver.Storage.Wal             ← Write-Ahead Log（group commit）
Quiver.Index                   ← B+Tree インデックス
Quiver.Storage.Records         ← Versioned Vertex / Edge / Nexus / owner-bound Property / Payload / Token ストア
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
| 新規 DB の初期確保 | デフォルト 1 MiB（8 KiB 境界） |
| ファイル成長 | 1 / 2 / 4 / 8 / 16 / 32 / 64 MiB の適応成長、1 回の上限はデフォルト 64 MiB |
| 文字列エンコーディング | UTF-8（長さプレフィックス付き） |
| 静止時のファイル | `*.quiver` 単一ファイル |
| 運用中のファイル | `*.quiver` + `*.quiver-wal` |
| format family | `QUIVER-SW` family version 2（旧 family からの自動移行なし） |
| primary property | `PropertyAddress` と 84B record の `PropertyVersionStore`。xmin、xmax、Generation は record 内に置き、public property ID と entity inline property は持たない |
| entity version sidecar | `EntityVersionMeta(xmin,xmax,generation)` の 24B record。page あたり 339 件、sidecar format version 4、旧 40B fallback なし |
| primary vector payload | 固定 tenant の `VectorPayloadStore`。generation、dimensions、element type、byte length、CRC32C を検証 |
| adjacency | `AdjacencySegmentStore` の単一 format。payload なしも `PayloadKind.None` で同形式 |
| ベクトル catalog | entry 長プレフィクス + per-index HNSW レイアウトパラメタ |

`TransactionManager` は database instance ごとの `WriterLease` と `SnapshotRegistry` を所有する。
facade と backend の既存開始 API は、内部の `BeginRead` と `BeginWrite` へ集約する adapter である。
read transaction は WAL を生成せず、writer と並行して開始時 snapshot を読む。
bulk、schema、maintenance の mutation 入口も同じ writer lease を取得する。
active writer の dirty page は commit fsync 前に data file へ書かない。
checkpoint は同じ writer lease で sharp boundary を作り、reader を待たずに committed dirty page と transaction catalog を flush する。

`QuiverDatabaseOptions.InitialFileAllocationBytes` と `MaximumFileGrowthStepBytes` で初期確保量と成長上限を変更できる。
いずれの値も 8 KiB 境界へ整列する。
WAL は `QUIVER-SW` file header、明示的な `Commit` レコード、ページイメージを使用する。
別 family、未知のレコード種別、切り詰め、checksum 不一致は open または解析時に拒否する。
recovery は有効な `Commit` を持つ winner の page image だけを page LSN 順に redoし、loser undo pass を持たない。
旧 DB と旧 WAL の読み替えは実装せず、ソースデータから再構築する。

### 開発中の format family version 運用（公開バージョンと分離する）

`StorageFormatVersion` と `WalFormat.FamilyVersion` は、利用者に公開する SemVer バージョン（`Directory.Build.props` の `VersionPrefix`）とは別物であり、連動させない。
開発中に両者を混同しないための運用規約を以下に定める。

- **pre-release 期の family version は clean break の内部カウンタとして扱う。**
  レイアウトを変える場合は DB と WAL の family version を同じ変更で更新し、旧 family の読み替えやマイグレーションを実装しない。
  DB は `StorageFormatMismatchException`、WAL は `WalFormatMismatchException` で fail-fast する。
- **公開バージョンは family version の増加回数に追随しない。**
  `VersionPrefix` は SemVer の意味論（[api-stability.md](../api-stability.md)）だけで上下する。
- **GA 直前に pre-release 期の format 履歴を単一のベースラインへ畳む。**
  中間 version の定数、decoder、fallback は残さない。
  DB 資産の移行は伴わない。
- **GA 後（`1.0.0` 以降）は §7.2 の互換規約に従う。**
  format 変更の可否は公開互換性ポリシーで判断する。
- **計画書では on-disk family version と公開 SemVer を明記して区別する。**

### ID 型

すべての識別子は `readonly record struct` で型安全に表現する。

```csharp
public readonly record struct VertexId(long Value);
public readonly record struct EdgeId(long Value);
public readonly record struct NexusId(long Value);
public readonly record struct NexusTypeId(int Value);
public readonly record struct PropertyAddress(EntityRef Owner, PropertyKeyId Key);
public readonly record struct LabelId(int Value);
public readonly record struct TransactionId(long Value);
// ... など
```

`-1` は「無効 / null」を意味する予約値。`VertexId.Value` は generation + sequence をパックした値で、
スロット再利用後も識別子の往復一貫性を保つ。
Property は entity ID を持たず、public cursor は version ref を公開しない。

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
using var db = QuiverDatabase.Open("./mygraph");

using (var tx = db.BeginTransaction())
{
    var alice = tx.CreateVertex("Person");
    tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
    tx.SetProperty(alice, "age",  PropertyValue.FromInt32(30));

    var bob = tx.CreateVertex("Person");
    tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));

    tx.CreateEdge(alice, bob, "KNOWS");
    tx.Commit();
}
```

型なしトラバーサル（`tx.G(db.Schema)` 起点。文字列キーで指定）:

```csharp
var g = tx.G(db.Schema);

var alice = g.AddVertex("Person").P("Name", "Alice").P("Age", 30).Next();
var bob   = g.AddVertex("Person").P("Name", "Bob").P("Age", 25).Next();
g.AddEdge("KNOWS").From(alice).To(bob).Next();

var names = g.Vertices().HasLabel("Person")
              .Has("Age", P.Gt(25L))
              .Out("KNOWS")
              .Values("Name")
              .ToList();   // → ["Bob"]

// 型なしエッジ経路
var recent = g.Vertex(alice).OutEdges("KNOWS").Has("since", P.Gt(2022L)).TargetVertex();
```

グラフパターンマッチ（Match DSL, Cypher の宣言的パターンに相当）:

```csharp
var results = g.Match(
    GraphPattern.Vertex("n", "Person")
                .Out("KNOWS", GraphPattern.Vertex("m", "Person"))
)
.Where("n", "Age", P.Gt(25L))
.Return(v => new
{
    PersonName = v["n"].Get<string>("Name"),
    FriendName = v["m"].Get<string>("Name"),
})
.ToList();

// サブトラバーサル述語（WHERE EXISTS / NOT EXISTS 相当）
var loners     = g.Vertices().HasLabel("Person").Not(t => t.Out("KNOWS")).ToList();
var connectors = g.Vertices().HasLabel("Person").Where(t => t.Out("KNOWS").HasLabel("Person")).ToList();

// ストリーミング（大量結果でメモリを抑える）
using var cursor = g.Vertices<Person>().AsCursor();
while (cursor.MoveNext())
{
    var person = cursor.Current;   // トランザクション有効期間内のみ有効
}
foreach (var name in g.Vertices().HasLabel("Person").Values("Name").AsEnumerable())
    Console.WriteLine(name);
```

### Source Generator 属性リファレンス

| 属性 | 対象 | 引数 | 省略時の挙動 |
|---|---|---|---|
| `[Vertex]` | クラス | `label` (省略可) | クラス名をラベルとして使用 |
| `[Edge]` | クラス | `type` (省略可) | クラス名をEdge型として使用 |
| `[Property]` | プロパティ | `key` (省略可) | プロパティ名をグラフキーとして使用 |
| `[Indexed]` | プロパティ | `indexName` (省略可) | `idx_{label}_{propertyName}` を自動生成。`[Property]` と併用必須 |
| `[Nexus]` | クラス | `type` (省略可) | クラス名をNexus型として使用 |
| `[Role]` | プロパティ | `role` (省略可) | プロパティ名をロール名として使用。型は `GraphVertexRef<TVertex>`（複数ロールは `IReadOnlyList<GraphVertexRef<TVertex>>`、省略可能ロールは nullable） |

> **注意:** クラス名・プロパティ名を変更すると `[Vertex]`・`[Indexed]` の自動生成名も変わり、既存
> インデックスファイルが孤立する。リネームの可能性がある場合は明示指定を推奨。
>
> **名前衝突:** 属性はすべて `Quiver.Api` 名前空間にある。`[Vertex]` / `[Property]` のような一般名は
> 他ライブラリの属性（例: FsCheck の `[Property]`）と衝突し得る。その場合は名前空間で限定する
> （例: `[Quiver.Api.Property]`）。

生成されるメソッド:

**`[Vertex]` クラス** — `Insert(tx, entity) → VertexId` / `InsertIndexed` / `Load(tx, id) → T` /
`Update(tx, id, entity)` / `Delete(tx, id)` / `FindBy{PropName}(tx, value) → List<(VertexId, T)>`（`[Indexed]` ごと）

**`[Edge]` クラス** — `Insert(tx, from, to, entity) → EdgeId` / `Load` / `Update` / `Delete`

**`[Nexus]` クラス** — `Insert(tx, entity) → NexusId` / `Load` / `Update`（プロパティのみ。
メンバー集合は作成時確定） / `Delete`、および型保存トラバーサル糖衣
`{Class}As{Prop}()` / `{Prop}()` / `Other{Prop}()`（ロールプロパティごと）

## Nexus（第一級 n 項Edge）

利用者向けの契約は [docs/spec/04_records_index.md](../spec/04_records_index.md#nexus-store)
（レコード）、[docs/spec/05_query.md](../spec/05_query.md#nexus-ops)（オペレータ / DSL / Match）、
[docs/spec/08_known_limits.md](../spec/08_known_limits.md#nexus-limits)（契約と限界）を正本とする。
サンプルは [samples/Quiver.Samples.Nexuses/](../../samples/Quiver.Samples.Nexuses/)。

### 実装マップ

| レイヤ | 主なファイル |
|---|---|
| Core ID / kind | `src/Quiver/Core/EntityRef.cs`（kind 付き packed identity）、`src/Quiver/Core/Ids.cs`（typed ID と Generation 込み equality）、`src/Quiver/Core/EntityId.cs`（Vertex / Edge / Nexus の strict internal tag） |
| ストア | `src/Quiver/Stores/VersionedNexusStore.cs`、`IncidenceStore.cs`、`VertexIncidenceHeadStore.cs`、`CoMembershipBlockStore.cs` |
| トランザクション | `src/Quiver/Transactions/TransactionManager.cs`、`WriterLease.cs`、`SnapshotRegistry.cs`、`TxNexusStore.cs`（snapshot / undo の配線） |
| 公開 CRUD | `src/Quiver/IGraphTransaction.cs`（`CreateNexus` / `DeleteNexus` / `GetMembers` / `GetNexuses` / プロパティ各種）、`ISchemaApi`（型 / ロールの token 管理） |
| クエリ | `src/Quiver/Operators/`（`AllNexusesScan` / `ExpandToNexus` / `ExpandMembers` / `CoMembership` の各 operator）、`src/Quiver/Query/PhysicalPlanner.cs` |
| DSL / Match | `src/Quiver/Client/GraphTraversalSource.cs`、`GraphTraversal.cs`、`Match/GraphPattern.cs`（`NexusPattern`） |
| SourceGen | `src/Quiver.SourceGen/GraphNexusGenerator.cs` / `GraphNexusModel.cs` / `GraphNexusEmitter.cs`、属性は `src/Quiver/Client/NexusAttribute.cs` |
| 保守 | `src/Quiver/Maintenance/Vacuum.cs`（`VacuumTarget.Nexuses`）、`DiagnosticsApi.CheckConsistency`、`GraphStats`（型別件数 / アリティ分布） |

### 固定 tenant（SingleFileContainer カタログ）

Nexus関連の論理ストアは次の固定 tenant を使う（変更しない）。

| tenant | 用途 |
|---|---|
| 7 | 欠番（旧 property entity sidecar 用。番号は詰めない） |
| 18 | nexus heap（header + property version chain head） |
| 19 | nexus の `ItemPointerMap` |
| 20 | nexus の MVCC / generation sidecar |
| 21 | incidence heap（27B fixed-slot、直接アドレス） |
| 22 | 欠番（旧 incidence 間接マップ用。番号は詰めない） |
| 23 | nexus type token |
| 24 | role token |
| 25 | vertex incidence head（6B sidecar） |
| 29 | primary vector payload metadata |
| 30 | primary vector payload blob |

### テスト

nexus の回帰は `tests/Quiver.Stores.Tests/IncidenceStoreTests.cs`、
`tests/Quiver.Tests/`（`NexusPropertyTests` / `NexusDiagnosticsTests` /
`NexusGeneratedCrudTests` / `NexusTypedTraversalTests` / `VacuumTests` / `GraphStatsTests` /
`CoMembershipBlockTests`）、`tests/Quiver.Client.Tests/`（`NexusRagQueryTests` /
`MatchPatternTests`）、`tests/Quiver.SourceGen.Tests/NexusGeneratorTests.cs` が担う。
性能ゲートの実測は「Nexus統合性能」節と
[docs/benchmarks/2026-07-06_HYP-6c_Nexus.md](../benchmarks/2026-07-06_HYP-6c_Nexus.md) を参照。

## 性能（詳細計測）

`benchmarks/Quiver.Benchmarks` の BenchmarkDotNet ベンチ、および standalone runner
`--basic-perf`（[BasicPerfRunner.cs](../../benchmarks/Quiver.Benchmarks/Standalone/BasicPerfRunner.cs)、
`dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --basic-perf`）で計測。
基本計測値は AMD Ryzen 7 5700X / .NET 10 / best-of-N の in-process Stopwatch（2026-06-09）。

| 操作 | 設計目標 | 実測 (2026-06-09) |
|---|---|---|
| Vertex作成（単一 tx 償却） | < 1 µs ※インメモリ操作目標 | ~3.5–4 µs/op（~250K ops/s） |
| Vertex作成 + プロパティ設定（同上） | < 2 µs | ~6 µs/op |
| Edge作成（同上） | — | ~7 µs/op（~140K ops/s） |
| 単発 durable commit（1 op = 1 commit、単一スレッド） | — | ~1.0 ms/commit（WAL flush 律速） |
| 1-hop scan（degree 100、AdjacencySegmentStore） | < 0.5 µs | ~0.35 µs（~3.5 ns/edge） |
| 1-hop scan（degree 100、linked-list / 索引なし） | — | ~11 µs（~0.11 µs/edge、MVCC 可視性込み） |
| BFS 2-hop（ハブ degree 100、leaf 10,000、adjacency segment） | < 5 ms | ~0.037 ms |
| 1-hop クエリ（`g.Vertex().Out()`、degree 100、adjacency segment） | クエリラッパ < 5% | ~4.2 µs/query（~42 ns/edge、生隣接の ~12×） |
| BulkLoader（10 万 edge） | 通常 TX 比 5× 以上高速 | 通常 TX（batch 1000）比 ~11.8× |

### 計測の要点

- **読み取りは隣接インデックスの有無で 30× 以上変わる。** `BeginBulkLoad(buildAdjacencyIndex: true)`
  で adjacency segment を構築すると 1-hop が ~3.5 ns/edge になり、索引なしの linked-list 経路
  （~0.11 µs/edge、MVCC 可視性チェック込み）より degree 100 で ~31× 速い。読み取り主体の
  ワークロードでは隣接インデックスを構築すること。
- **バッファプールの checksum 検証は disk→frame ロード時のみ行う（pin ごとには再計算しない）。**
  常駐フレームの内容は disk 破損に晒されず、書込は `UnpinDirty` で CRC を更新し `FrameLock` が
  read/write pin を排他するため、pin ごとの全 8KB CRC32 は冗長だった。撤去後も torn write / bit rot の
  ロード時検出は維持（crash contract / chaos テストで担保）。この 1 点で linked-list 1-hop が
  ~2.3 → ~0.11 µs/edge（~20×）短縮した。
- **書き込みは単発 durable commit が ~1 ms（WAL flush 律速）。** 大量書き込みは 1 tx にまとめる
  （償却 ~3.5–4 µs/vertex）か BulkLoader を使う。並行 commit では group commit
  （`QuiverDatabaseOptions.GroupCommitWindow`）でスループットが桁違いに上がる
  （64-thread で window=0 比 ~28×、別計測 FT-27）。
- **WAL page-image の Encode（trim+RLE）は commit 時にページ毎 1 回だけ行う（書込ごとには行わない）。**
  トランザクション内で同一ページを繰り返し書いても WAL に出るのは最終状態 1 件（latest-wins coalesce）
  なので、中間状態の Encode は無駄。これを `FlushPending`（commit）へ遅延し、ホットページ反復書込
  （version sidecar / record heap）の増幅を解消。単一 tx 償却の書込が ~4×
  高速化した（Vertex作成 ~14 → ~3.5–4 µs/op）。
- **クエリ DSL の 1-hop（degree 100）は ~4.2 µs/query（~42 ns/edge、生隣接の ~12×）。**
  プラン構築 + 物理オペレータ生成は ~0.4 µs と僅少。結果行ごとの VertexId 世代スタンプは、スロット
  再利用（vacuum 回収）が無い間は version sidecar 読み取りを省く高速パスで処理する。これで 1-hop
  クエリは ~62 → ~8 µs/query（~7.7×）に短縮し、さらに checksum-at-load で adjacency segment の pin が安くなり
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

### Nexus統合性能（HYP-6c 統合ゲート）

第一級Nexusの走査・書き込み・Match を製品 API（`QuiverDatabase` / DSL / Match）経由で
再測定した（AMD64 / .NET 10.0.9 / workstation GC）。三ゲート全て合格。詳細:
[docs/benchmarks/2026-07-06_HYP-6c_Nexus.md](../benchmarks/2026-07-06_HYP-6c_Nexus.md)。
runner: `--nexus-traversal` / `--nexus-write` / `--nexus-match`。

- **走査**（`g.Vertex(hub).Nexuses("Fact","subject").OtherMembers("object")` の co-membership view
  vs binary `Out` 1-hop、arity 4）: p50 比 degree 10/100/1000 = 1.15x / 0.95x / 2.14x（ゲート ≤3x 合格）。
  ビュー未登録のリンクチェーン fallback は 3.59x / 2.80x / 4.57x。
- **書き込み**（arity 2/4/8/16）: create WAL 増幅 1.249x / 1.890x / 3.182x / 5.746x（ゲート
  `(1+arity/2)×` = 2/3/5/9 以内、HYP-2d の WAL バイトを製品 API で再現）。作成遅延 48〜220 µs/op、
  プロパティ書込み ~11〜14 µs/op、削除 ~4〜5 µs/op。
- **高次数 DeleteVertex カスケード**（1 vertex が 10^3 / 10^4 nexus のメンバー）: tx 7.84 ms / 47.69 ms、
  WAL 61 KB / 608 KB、デッドロック無し、削除後 `CheckConsistency` は 0 件。
- **Match**: 四役割の星型 Match は等価な reified graph pattern（vertex + MEMBER edge の 4-way 結合）の
  0.71x（facts=1000 で 1.960 ms vs 2.768 ms）。

## 開発状況

現在の実装トラックは [Single Writer + Snapshot Readers 抜本再設計](../../plans/single-writer-redesign.md) である。

`docs/spec/` は current as-built を記録する。

target の設計は計画書を正本とし、実装されるまで as-built として記述しない。

過去トラックの完了記録は historical record として残す。

それらは commit hash、当時の API、実測値を説明するが、現行の実装順序や再実装禁止の根拠にはならない。

再設計の disposition が Delete または Rewrite を指定するコードは、過去の完了記録に関わらず対象になる。

### ローカル運用

bootstrap は `develop` で行い、その後の tracked な再設計作業は `redesign/single-writer` の専用 worktree で行う。

専用 worktree は同じ Git repository の履歴、tag、index、remote を共有する。

物理コピーした別ライブラリや、後日の成果物差し替えで並行開発しない。

この作業ではユーザー指示により外部 push と upstream 設定を保留している。

ローカル commit と tag は有効な進行記録だが、remote との同期を意味しない。

外部公開や `develop` への統合を再開する前に、[再設計の実行手順](../../plans/single-writer-redesign-process.md) §2〜§6 で定める未実施条件を満たす。

`.agents/` と `.claude/` は git 管理外のローカル設定である。

両方の `quiver-implement` mirror は byte-for-byte で一致させ、tracked commit に混ぜない。

専用 worktree には mirror が複製されないため、必要なときはメインツリー側を read-only で参照する。

### Wave gate

各 Wave の統合候補は、機能 test、crash test、baseline gate、as-built 更新を満たす。

crash test と baseline gate を `N/A` とするときは、対象挙動を変更していないことを差分で示す。

solution build、変更した contract の as-built 更新、Wave 固有の機能 test は `N/A` にできない。

性能 gate が未達なら、原因と再設計案を plan の decision log に追記してからユーザー判断を得る。

## 設計ドキュメント

| ファイル | 内容 |
|---|---|
| [00_conventions.md](00_conventions.md) | 共通規約（命名・性能指針・テスト規約） |
| `01_storage_paging.md` | historical record: ページ管理・バッファプール |
| `02_record_codec.md` | historical record: バイト列直接操作プリミティブ |
| `03_fixed_record_stores.md` | historical record: Vertex / Edge ストア |
| `04_property_token_stores.md` | historical record: Property / Token ストア |
| `05_btree_index.md` | historical record: B+Tree インデックス |
| `06_wal.md` | historical record: Write-Ahead Log |
| `07_transaction_recovery.md` | historical record: トランザクション・リカバリ |
| `08_physical_operators.md` | historical record: Volcano 型物理演算子 |
| `09_graph_api.md` | historical record: 公開 CRUD API |
| `10_embedding_pipeline.md` | historical record: 埋め込み / ベクトル検索パイプライン |
| `11_rearchitecture_master_plan.md` | historical record: 抜本再設計マスタープラン |
| `12_rag_backend_direction.md` | historical record: ローカル RAG バックエンド方向性（ポジショニング・非目標の正本） |
| `13_fulltext_search.md` | historical record: 全文検索 / ハイブリッド検索（転置インデックス + BM25 + RRF） |
| `14_rag_layer.md` | historical record: Quiver.Rag レイヤ（Document/Chunk スキーマ・取込・検索） |

## エージェント運用ガードレール

コーディングエージェント (Claude Code / Codex) が規約を破らないための決定論的バックストップ。
CLAUDE.md / AGENTS.md の散文だけではエージェント自身の判断に依存し、指示が無視されうる。
そこで機械的に検査するフックを併用する。設計思想はこの節を正本とする。

### 原則: パターンヒットは signal であって verdict ではない

正規表現の一致はあくまで「候補シグナル」として扱い、ハードブロックはしない。

- **advisory を優先する。** 書き込み前に拒否 (PreToolUse block) すると、誤検知時にエージェントが
  回避を繰り返して会話が破綻する。書き込み後に助言を返す (PostToolUse) なら、正当なら無視でき、
  破綻しない。
- **誤検知の主因はパスで決定論的に消す。** 例外地 (`plans/`・`docs/design/`・エージェント内部
  ツールの `.claude/`・`.agents/`) を先に除外すれば、意味判断を持ち出す前に大半の誤検知が消える。
- **検査は変更差分に絞る。** ファイル全体を毎回再検査すると、既存の記号 (`tests/`・`benchmarks/`
  には大量にある) を再検知して騒がしくなる。その編集が「新規に書いたテキスト」だけを見る。
- **意味判断が本当に必要になったときだけ LLM 層へ escalation する。** 決定論版がノイズ過多だと
  実証されたら、小型 LLM に候補の意味 (本当に危険か / 過去の完了報告か / 仮定か / ユーザ質問か)
  を判定させる 2 層目を足す。その際は prompt injection 対策・secret 秘匿・再帰ガードが必須。
  現状は決定論の Layer 1 のみで足りており、未導入。

### 実装

正本は `scripts/agent-guardrails/check-track-markers.ps1`（検出ロジックの単一置き場）。
現在の対象規約は「タスク管理番号 (例 `HYP-7`) や `案A`/`案B` を、`plans/`・`docs/design/` 以外の
`src` コメント・識別子・公開 docs に残さない」。検出接頭辞の allowlist はスクリプト先頭が正本
（技術用語 `UTF-8` / `AVX-512` 等を誤検知しないよう明示列挙）。

| 呼び出し口 | 用途 | 挙動 |
|---|---|---|
| `-Hook`（`.claude/settings.json` の PostToolUse） | Claude Code | 編集差分のみ検査し advisory 通知 (exit 2)。ブロックしない |
| `-Scan` | 人間 / CI / Codex の監査 | 対象ルートを一覧監査 (exit 1)。tests/benchmarks は既存ベースラインが多い |
| `-DiffAgainst <ref>` | Codex / commit 前の監査 | ref から追加された行だけを検査し、既存候補と新規漏出を分離する |
| `<path>` | 手動 / スクリプト | 指定ファイルを検査 |

両エージェントで同一ロジックを共有する: Claude Code はフックから、Codex / 人間は `-Scan` /
パス指定から同じスクリプトを呼ぶ。`.claude/settings.json` は追跡外 (ローカル) だが、規約とロジックの
正本はこの節と追跡されるスクリプトにあるため、参照先は一元化される。

`check-markdown-links.ps1 -Roots <paths...>` は tracked Markdown の相対 link の解決先を検査する。

`check-skill-redirects.ps1` が認める historical redirect は、Claude 側の `SKILL.md` の唯一の行である次の形式だけである。

```text
<!-- quiver-historical-skill-redirect: ../../../../.agents/skills/quiver-implement/comlpeted/SKILL.md -->
```

redirect は ClaudeRoot 配下から AgentsRoot 配下の leaf `SKILL.md` への相対 forward-slash path でなければならない。

本文併記、multi-hop、通常の Markdown または YAML、絶対 URI、drive path は redirect として認めない。

### Wave 0 監査記録

2026-07-12 に `redesign-baseline` を基準として Wave 0 の gate を監査した。

`check-track-markers.ps1 -DiffAgainst redesign-baseline` は新規候補0件で成功した。

full `-Scan` は既存候補184件を検出した。
これは Wave 10 の cleanup baseline として記録し、Wave 0 の差分 gate とは区別する。

`README.md`、`docs/spec`、`docs/design` の Markdown relative link audit は成功した。

historical docs と `plans/` を含む全 tracked Markdown の監査は既存の欠落26件を検出した。
この監査は historical debt の記録であり、Wave 0 の hard pass にはしない。

3つの guardrail self-test は成功した。
`check-skill-redirects.ps1` は local mirror の historical redirect を解決し、Windows の physical AgentsRoot escape を junction fixture で拒否した。
active `quiver-implement` mirror は SHA-256 で一致した。

focused `tools/Quiver.Studio` build と `dotnet build Quiver.slnx -v minimal` は、ともに0 warnings、0 errorsで成功した。

`git diff redesign-baseline -- src/Quiver/Transactions src/Quiver/Wal src/Quiver/Storage` と `git diff redesign-baseline -- src tests benchmarks` は差分なしだった。

## Versioning / API 安定性

Quiver は [Semantic Versioning](https://semver.org/lang/ja/)（`MAJOR.MINOR.PATCH`）に従う。
MAJOR は breaking change、MINOR は後方互換な機能追加、PATCH はバグ修正のみ。`1.0.0` 未満（`0.x`）は
安定性の保証対象外。バージョンの正本は [Directory.Build.props](../../Directory.Build.props) の `VersionPrefix`。

安定性を保証する public API は `Quiver` / `Quiver.Api` / `Quiver.Core` の public 型に限る。非推奨化は
最低 1 MINOR の `[Obsolete]` 告知期間を置いてから次の MAJOR で削除する。

public API surface は [tests/Quiver.PublicApi.Tests/](../../tests/Quiver.PublicApi.Tests/) の approval test
（`PublicApiGenerator`）で機械的に固定されており、意図しない breaking change は CI で検出される。
詳細は [docs/api-stability.md](../api-stability.md) を参照。

## NuGet パッケージ化

### 公開パッケージ

`dotnet pack Quiver.slnx` で以下 4 つのライブラリが NuGet パッケージ (`.nupkg` + symbol `.snupkg`) になる。
テスト / ベンチ / サンプル / sandbox は `IsPackable=false`（[Directory.Build.props](../../Directory.Build.props) の既定）で除外される。

| パッケージ | 内容 | 依存 |
|---|---|---|
| `Quiver` | コアエンジン（型付き属性は本体に内包 + Source Generator を**同梱**） | System.IO.Hashing |
| `Quiver.Hosting` | `Microsoft.Extensions.Hosting` / DI 統合 | `Quiver`, Microsoft.Extensions.* |
| `Quiver.OpenTelemetry` | OpenTelemetry 計装登録 | `Quiver`, OpenTelemetry(.Api) |
| `Quiver.Rag` | ローカル RAG スキーマ層 | `Quiver` |

#### incubating（非公開）

`Quiver.Embedding`（テキスト埋め込みパイプライン、VEC-4）は**現状 NuGet 公開しない**（`IsPackable=false`、
リポジトリには残しビルド/テスト対象）。理由: ①具体プロバイダ未同梱で単体では動かない（`IEmbeddingProvider` を
利用者が実装する必要がある）②実消費者が単体テストのみ ③主用途の `Quiver.Rag` はチャンク埋め込みを
`IChunkEmbedder` のインライン注入で行い本パイプラインを使わない。参照プロバイダ実装・サンプル・実消費者
（例: RAG の遅延/バックグラウンド埋め込みモード）が揃った時点で `IsPackable=true` にして公開へ昇格する。

型付きエンティティ属性（`[Vertex]` / `[Edge]` / `[Property]` / `[Indexed]`、namespace `Quiver.Api`）は
**`Quiver` 本体アセンブリに内包**している（[src/Quiver/Client/VertexAttribute.cs](../../src/Quiver/Client/VertexAttribute.cs)・
[EdgeAttribute.cs](../../src/Quiver/Client/EdgeAttribute.cs)）。`Quiver.SourceGen`（Roslyn generator）は
**単体公開せず** `Quiver` パッケージへ analyzer として同梱する（`analyzers/dotnet/cs/Quiver.SourceGen.dll`、
[src/Quiver/Quiver.csproj](../../src/Quiver/Quiver.csproj) の `_QuiverAddBundledAnalyzer` target）。生成器は属性を
**完全修飾名の文字列**で照合する（`GraphVertexGenerator.VertexAttributeFqn = "Quiver.Api.VertexAttribute"` 等）ため、
属性アセンブリへの参照は不要。SourceGen の `ProjectReference` は `PrivateAssets="all"` でパッケージ依存に昇格させない。

結果、利用者は `Quiver` パッケージ 1 つの参照で属性 + 生成器まで揃う。さらに `Quiver` は
[build/Quiver.props](../../src/Quiver/build/Quiver.props) を `build/`・`buildTransitive/` に同梱し、`ImplicitUsings`
有効なプロジェクトには `Quiver` / `Quiver.Api` の global using を自動注入する（`using` 文ゼロのドロップイン。
不要なら利用者側で `<Using Remove="Quiver.Api" />` で opt-out 可）。

> リポジトリ内のテスト/サンプルは `Quiver` を `ProjectReference` するが、analyzer は `PrivateAssets="all"` で
> transitive には流れない。そのため `[Vertex]` 等を使うプロジェクトは `Quiver.SourceGen` を analyzer として
> 直接参照する（`Quiver.Tests` / `Quiver.Client.Tests` / `Samples.SourceGen` / `QuiverSandbox` / `SourceGen.Tests`）。
> 属性型は `Quiver` 本体から供給されるので、属性アセンブリの直接参照は不要。

### 共通メタデータ / 設定

パッケージ共通のメタデータ（Authors / ライセンス `MIT` / `RepositoryUrl` / `PackageReadmeFile` /
`PackageIcon` 等）は [Directory.Build.props](../../Directory.Build.props) に一元化。README（リポジトリルートの
[README.md](../../README.md)）とアイコン（`icon.png`）の同梱は [Directory.Build.targets](../../Directory.Build.targets) で
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

CI では [.github/workflows/release.yml](../../.github/workflows/release.yml) が `v*` タグ push を契機に
pack → `nuget.org` へ push する（API キーは GitHub secret `NUGET_API_KEY`）。手動実行
（`workflow_dispatch`）では artifact 生成のみ。

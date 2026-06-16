# Quiver Cookbook

Quiver の典型ユースケースをすぐに動かせるレシピ集。各レシピは [`samples/`](../samples/) のいずれかと対応している。

> **運用 (バックアップ / チューニング / 障害復旧 / 既知の制約) を探している場合は
> [docs/operations/](operations/README.md) を参照。** こちらは API レシピ集、あちらは「どう設定すれば速いか /
> 壊れた DB をどう直すか」を扱う運用者向けドキュメント。

---

## 1. 重み付き shortest-path (Dijkstra / A*)

エッジに重みプロパティを乗せ、重み合計が最小の経路を求める。
`g.WeightedShortestPath(...)` は距離だけでなく経路 (ノード列 / エッジ列) も返す。

```csharp
var g = tx.G(db.Schema);

// 重み付きの道路網を構築
var s = g.AddNode("Junction").P("name", "S").Next();
var a = g.AddNode("Junction").P("name", "A").Next();
var b = g.AddNode("Junction").P("name", "B").Next();
var t = g.AddNode("Junction").P("name", "T").Next();

g.AddRelationship("ROAD").From(s).To(a).P("weight", 1.0).Next();
g.AddRelationship("ROAD").From(s).To(b).P("weight", 5.0).Next();
g.AddRelationship("ROAD").From(a).To(t).P("weight", 2.0).Next();
g.AddRelationship("ROAD").From(b).To(t).P("weight", 1.0).Next();

// 重み付き最短経路 (Dijkstra)。weight プロパティをエッジ重みとして読む。
var path = g.WeightedShortestPath(s, t, weightKey: "weight", type: "ROAD");
if (path.Found)
    Console.WriteLine($"S→T 最短重み: {path.Distance}, 経由ノード数: {path.Nodes.Count}");
//  → S→A→T (1.0 + 2.0 = 3.0) が S→B→T (5.0 + 1.0 = 6.0) より短い

// ホップ数だけが必要なら従来どおり ShortestPathTo も使える
var hops = g.Node(s).ShortestPathTo(t, type: "ROAD").TryNext();
```

> A* を使うときは `WeightedShortestPathAStar(s, t, "weight", "x", "y")` でノードの座標プロパティから
> ヒューリスティックを自動生成するか、`WeightedShortestPath(s, t, "weight", heuristic: node => ...)` で
> 推定残コストを渡す。ヒューリスティックが admissible (consistent) なら Dijkstra と同じ最適解を、
> より少ないノード展開で得られる。エッジ重みは非負である必要がある。

---

## 2. MERGE で upsert

冪等な書き込みパターン。同じキーで何度実行しても重複ノードを増やさない。

> `MergeNode` は `(label, matchKey)` のインデックスが登録されていれば O(log n) シークを使い、無ければラベル内全スキャンに落ちる (ノード数次第で秒オーダー、PW-18 参照)。MERGE を多用する業務キーには事前に `Schema.CreateIndex` を呼んでおく。

```csharp
// データベース起動直後に一度だけ
db.Schema.CreateIndex("idx_person_email", "Person", "email", IndexKind.StringEquality);

using var tx = db.BeginTransaction();

var (id, created) = tx.MergeNode(
    "Person",
    "email",
    PropertyValue.FromString("alice@example.com"));

if (created)
{
    tx.SetProperty(id, "createdAt", PropertyValue.FromInt64(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
}

// 毎回更新したいフィールド
tx.SetProperty(id, "lastSeenAt", PropertyValue.FromInt64(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
tx.Commit();
```

---

## 3. VEC で類似検索 + フィルタ

ベクトル類似度上位 k 件を取得しつつ、グラフ側の述語で絞り込む典型 RAG パターン。

```csharp
// graph-first: 先にラベル/プロパティで絞ってから KNN
var candidates = g.Nodes().HasLabel("Document")
                  .Has("language", "ja")
                  .FilterByKnn("doc_v1", queryVec, k: 20)
                  .Has("isPublic", true)
                  .ToList();

// vec-first: 先に KNN で広く拾ってから後段でフィルタ
var hits = g.Knn("doc_v1", queryVec, k: 100)
            .HasLabel("Document")
            .Has("language", "ja")
            .Limit(20)
            .ToList();
```

---

## 4. Match DSL でグラフパターン抽出

Cypher の `MATCH (n:Person)-[:KNOWS]->(m:Person)` 相当のパターンを書く。

```csharp
var pairs = g.Match(
    GraphPattern.Node("n", "Person")
                .Out("KNOWS", GraphPattern.Node("m", "Person"))
)
.Where("n", "age", P.Gt(25L))
.Return(v => new
{
    PersonName = v["n"].Get<string>("name"),
    FriendName = v["m"].Get<string>("name"),
})
.ToList();
```

---

## 5. BulkLoader による大量データ投入

1000 万エッジ級の初期インポートでは `BeginStreamingBulkLoad` を使ってピークヒープを抑える。

```csharp
using var loader = db.BeginStreamingBulkLoad(buildAdjacencyIndex: true);

var personLabel = db.Schema.GetOrCreateLabel("Person");
var knowsType   = db.Schema.GetOrCreateRelationshipType("KNOWS");

for (long i = 0; i < 10_000_000; i++)
    loader.AppendNode(new NodeId(i), personLabel);

for (long i = 0; i < 9_999_999; i++)
    loader.AppendRelationship(new RelationshipId(i), new NodeId(i), new NodeId(i + 1), knowsType);

loader.Commit();
```

---

## 6. SourceGenerator で型安全 CRUD

ボイラープレートを削減し、リファクタリング耐性を上げる。

> `[Indexed]` は SourceGenerator に `InsertIndexed` / `FindByName` および属性情報からインデックスを作成する `EnsureIndexes` / `CreateIndex` の生成を指示するマーカー。実体インデックスは `db.EnsureIndexes<T>()` (一括) もしくは `db.CreateIndex<T>(p => p.Prop)` (単一) で作成する。文字列直書きの `db.Schema.CreateIndex(...)` も引き続き使えるが、属性値との二重管理になる。

```csharp
[Node]
public partial class Person
{
    [Indexed]   // SourceGen マーカー — 実体インデックスは下で作成
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

// 初期化時に一度だけ — [Indexed] 付きプロパティを SourceGen 情報からまとめて作成。
db.EnsureIndexes<Person>();

using var tx = db.BeginTransaction();
var g = tx.G(db.Schema);

var id = g.InsertIndexed(new Person { Name = "Alice", Age = 30 });
var loaded = g.Load<Person>(id);
loaded.Age = 31;
g.Update(id, loaded);

var found = Person.FindByName(tx, "Alice");
```

---

## 7. WAL クラッシュリカバリの確認

WAL PageImage replay (FT-9) によりコミット済みデータはクラッシュ後も完全復元される。

```csharp
NodeId savedId;

// 書き込み
using (var db = GraphDatabase.Open(dir))
using (var tx = db.BeginTransaction())
{
    savedId = tx.CreateNode("Config");
    tx.SetProperty(savedId, "version", PropertyValue.FromString("1.0"));
    tx.Commit();
}

// 再オープン: コミット済みデータは復元される
using (var db = GraphDatabase.Open(dir))
using (var tx = db.BeginTransaction())
{
    System.Diagnostics.Debug.Assert(tx.NodeExists(savedId));
}
```

---

## 8. 統計取得と整合性チェック

運用観測のエントリ。

```csharp
var stats = db.Diagnostics.GetStatistics();
Console.WriteLine($"Nodes={stats.NodeCount}, Rels={stats.RelationshipCount}");
Console.WriteLine($"BufferPool ヒット率 = {stats.BufferPoolHits} / {stats.BufferPoolHits + stats.BufferPoolMisses}");

var report = db.Diagnostics.CheckConsistency();
if (!report.IsConsistent)
{
    foreach (var issue in report.Issues)
        Console.WriteLine($"  問題: {issue}");
}
```

---

## 9. dotnet-counters でリアルタイム観測 (OB-2)

`Quiver-EventSource` は in-box (追加 NuGet 不要) で公開される `EventSource`。
別ターミナルから `dotnet-counters` を当てるだけで、buffer-pool / WAL / トランザクション /
ロック / 索引 / vacuum の主要メトリクスを 1 秒粒度で観測できる。

```pwsh
# 1) インストール (初回のみ)
dotnet tool install -g dotnet-counters

# 2) Quiver を埋め込んだプロセスの PID を調べる
dotnet-counters ps

# 3) Quiver の全メトリクスをリアルタイム表示
dotnet-counters monitor -n <YourProcessName> --counters Quiver-EventSource

# あるいは PID 指定:
dotnet-counters monitor -p <pid> --counters Quiver-EventSource
```

公開メトリクス (一部抜粋):

| 名前 | 種類 | 説明 |
|---|---|---|
| `buffer-pool-hit-ratio` | gauge | フレーム再利用率 (hits / (hits+misses)) |
| `buffer-pool-evictions` | gauge | 累計 eviction 件数 |
| `buffer-pool-size-bytes` | gauge | 全 PagedFile のバッファプール総バイト数 |
| `wal-bytes-per-sec` | rate | WAL 追記スループット |
| `wal-pending-flush-count` | gauge | fsync 待ちの FlushTo 件数 |
| `current-checkpoint-threshold-bytes` | gauge | Fixed / Adaptive 現在値 (FT-28) |
| `active-tx-count` | gauge | アクティブな transaction 数 |
| `tx-commit-per-sec` | rate | コミットスループット |
| `tx-abort-per-sec` | rate | アボートスループット |
| `tx-deadlock-victim-count` | rate | DeadlockDetector が中断した犠牲者 / 秒 (FT-25) |
| `lock-wait-avg-ms` | gauge | 平均ロック取得待ち (ms) |
| `lock-contention-count` | gauge | 累計コンテンション件数 |
| `index-orphan-count` | gauge | `CheckIndexConsistency()` 最新観測の orphan 件数 (FT-22) |
| `vacuum-progress-percent` | gauge | vacuum 実行中の進捗 (0 = 非実行) (OP-3) |
| `crash-recovery-count` | rate | crash recovery 起動回数 (通常 0) |

`Quiver-EventSource` を有効化しない限り PollingCounter は生成されないので、
本機能の overhead は実質ゼロ。OpenTelemetry 経由でメトリクスを送りたい場合は
`Quiver.OpenTelemetry` パッケージの `AddQuiverInstrumentation()` を使う (OB-1)。

---

## 10. ローカル RAG (Quiver.Rag)

別アセンブリ `Quiver.Rag` は、文書 → チャンク格納・取込/再取込・ハイブリッド検索 +
graph expansion の定型を 1 API で提供する。エンジン本体 (`Quiver`) のみに依存し、埋め込み生成は
呼び出し側が `IChunkEmbedder` を注入する。サンプルは
[`samples/Quiver.Samples.Rag`](../samples/Quiver.Samples.Rag/)。

```csharp
using Quiver;
using Quiver.Rag;

using var db = GraphDatabase.Open("rag.quiver");

// 索引 (sourceId / ベクトル / 全文) はコンストラクタで冪等作成される。
var store = new RagStore(db, new RagStoreOptions
{
    EmbeddingDimensions = embedder.Dimensions,            // 注入する埋め込み器と一致させる
    Chunking = new ChunkingOptions { TargetSize = 800, Overlap = 100 },
});

// 取込: 取込側 (PdfTools 等) が読み順復元・正規化したブロック列を渡す。
var doc = new IngestedDocument(
    SourceId: "docs/intro.md",
    Title: "はじめに",
    Metadata: new Dictionary<string, string> { ["category"] = "guide" },
    Blocks: new[]
    {
        new IngestedBlock(BlockKind.Heading, "概要", HeadingLevel: 1),
        new IngestedBlock(BlockKind.Paragraph, "本文の段落 ..."),
    });

var result = await store.UpsertDocumentAsync(doc, embedder);
// result.Unchanged == true なら contentHash 一致の no-op (再取込はべき等)。

// 検索: queryText + queryVector の両方で RRF ハイブリッド、片方だけでも可。
var searcher = new RagSearcher(store);
IReadOnlyList<RagHit> hits = searcher.Search(
    queryText: "Zphobos",
    queryVector: await EmbedQueryAsync("周辺の文脈"),   // null なら BM25 のみ
    new RagSearchOptions
    {
        K = 10,
        NeighborExpansion = 1,                          // NEXT_CHUNK 前後 1 件を連結
        // 等値メタデータは MetadataEquals で渡すと検索前に母集団を絞り込む (push-down)。
        MetadataEquals = new Dictionary<string, string> { ["category"] = "guide" },
    });

foreach (var h in hits)
    Console.WriteLine($"#{h.Rank} 〈{h.Document.Title}〉{h.HeadingPath}: {h.ChunkText}");

// 文書差し替え (内容変更時) と削除。どちらも単一トランザクションで原子的。
await store.UpsertDocumentAsync(updatedDoc, embedder);  // 旧チャンクを消して入れ直す
store.DeleteDocument("docs/intro.md");
```

> 取込側 (PdfTools 等) が `IngestedDocument` を JSON でやり取りする場合は、契約の正本
> `IngestedDocumentJson.Serialize` / `.Deserialize` を参照する (camelCase / `BlockKind` は文字列 /
> null フィールド省略、source-gen で NativeAOT 安全)。境界の JSON 規約が両側で一致し続ける。

**運用上の注意:**

- **再取込のべき等性は `contentHash`（Blocks のハッシュ）で判定する。** Blocks を変えずに
  `Title` / `Metadata` だけ変更しても no-op になり既存値が保たれる (再チャンク・再埋め込み回避)。
- **差し替え・削除は単一トランザクション。** 取込中にプロセスが落ちても「旧版が無傷」か
  「新版が完全」のどちらかで、中間状態は残らない。クラッシュ安全性はこの原子性に委ねている。
- **埋め込みはトランザクションの外で先に実行される。** `IChunkEmbedder` が失敗しても DB は無変更。
  `Dimensions` は `RagStoreOptions.EmbeddingDimensions` と一致している必要がある。
- **見出し語は BM25 でも引ける。** 見出しパスを前置した `searchText` を全文索引の対象にしているため、
  本文に出てこない見出しの語も検索でヒットする (`text` プロパティは原文スライスのまま保たれる)。
- **メタデータの絞り込みは 2 系統。** 等値条件は `MetadataEquals` (検索前に母集団を絞る candidate-side
  push-down → recall hole が起きない)。範囲条件など複雑な述語は `MetadataFilter` (後段フィルタ。上位 K 件
  取得後に除外するため、フィルタが大半を弾くと該当文書があっても 0 件になり得る)。
- **頻繁な再取込でも ANN グラフは劣化しにくい。** 削除/上書きで HNSW を再リンクし (近傍修復 + 物理削除)、
  削除が累積すると自動再構築する。長期間きわめて高頻度の churn を続ける場合のみ手動再構築を検討する。
- 全文索引が未作成の場合 `RagStore.FullTextEnabled == false` となり、
  検索は KNN のみで動作する。


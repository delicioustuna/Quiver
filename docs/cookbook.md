# Quiver Cookbook

Quiver の典型ユースケースをすぐに動かせるレシピ集。各レシピは [`samples/`](../samples/) のいずれかと対応している。

> **運用 (バックアップ / チューニング / 障害復旧 / 既知の制約) を探している場合は
> [docs/operations/](operations/README.md) を参照。** こちらは API レシピ集、あちらは「どう設定すれば速いか /
> 壊れた DB をどう直すか」を扱う運用者向けドキュメント。

---

## 1. 重み付き shortest-path (Dijkstra / A*)

エッジに重みプロパティを乗せ、重み合計が最小の経路を求める。
`g.WeightedShortestPath(...)` は距離だけでなく経路 (Vertex列 / エッジ列) も返す。

```csharp
using var tx = db.BeginWriteTransaction();
var m = tx.Mutate;
var g = tx.Query;

// 重み付きの道路網を構築
var s = m.AddVertex("Junction").P("name", "S").Next();
var a = m.AddVertex("Junction").P("name", "A").Next();
var b = m.AddVertex("Junction").P("name", "B").Next();
var t = m.AddVertex("Junction").P("name", "T").Next();

m.AddEdge("ROAD").From(s).To(a).P("weight", 1.0).Next();
m.AddEdge("ROAD").From(s).To(b).P("weight", 5.0).Next();
m.AddEdge("ROAD").From(a).To(t).P("weight", 2.0).Next();
m.AddEdge("ROAD").From(b).To(t).P("weight", 1.0).Next();

// 重み付き最短経路 (Dijkstra)。weight プロパティをエッジ重みとして読む。
var path = g.WeightedShortestPath(s, t, weightKey: "weight", type: "ROAD");
if (path.Found)
    Console.WriteLine($"S→T 最短重み: {path.Distance}, 経由Vertex数: {path.Vertices.Count}");
//  → S→A→T (1.0 + 2.0 = 3.0) が S→B→T (5.0 + 1.0 = 6.0) より短い

// ホップ数だけが必要なら従来どおり ShortestPathTo も使える
var hops = g.Vertex(s).ShortestPathTo(t, type: "ROAD").TryNext();
tx.Commit();
```

> A* を使うときは `WeightedShortestPathAStar(s, t, "weight", "x", "y")` でVertexの座標プロパティから
> ヒューリスティックを自動生成するか、`WeightedShortestPath(s, t, "weight", heuristic: vertex => ...)` で
> 推定残コストを渡す。ヒューリスティックが admissible (consistent) なら Dijkstra と同じ最適解を、
> より少ないVertex展開で得られる。エッジ重みは非負である必要がある。

---

## 2. MERGE で upsert

冪等な書き込みパターン。同じキーで何度実行しても重複Vertexを増やさない。

> `MergeVertex` は `(label, matchKey)` のインデックスが登録されていれば O(log n) シークを使い、無ければラベル内全スキャンに落ちる (Vertex数次第で秒オーダー)。MERGE を多用する業務キーには事前に `EditSchema` で索引を作成しておく。

```csharp
// データベース起動直後に一度だけ
using (var schemaTx = db.BeginWriteTransaction())
{
    schemaTx.EditSchema.CreateIndex(new ScalarIndexDefinition(
        "idx_person_email",
        new PropertyTarget(PropertyOwnerKind.Vertex, "email", "Person"),
        IndexKind.StringEquality));
    schemaTx.Commit();
}

using var tx = db.BeginWriteTransaction();

var (id, created) = tx.MergeVertex(
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
var candidates = g.Vertices().HasLabel("Document")
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
    GraphPattern.Vertex("n", "Person")
                .Out("KNOWS", GraphPattern.Vertex("m", "Person"))
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

LabelId personLabel;
EdgeTypeId knowsType;
using (var schemaTx = db.BeginWriteTransaction())
{
    personLabel = schemaTx.EditSchema.GetOrCreateLabel("Person");
    knowsType = schemaTx.EditSchema.GetOrCreateEdgeType("KNOWS");
    schemaTx.Commit();
}

for (long i = 0; i < 10_000_000; i++)
    loader.AppendVertex(new VertexId(i), personLabel);

for (long i = 0; i < 9_999_999; i++)
    loader.AppendEdge(new EdgeId(i), new VertexId(i), new VertexId(i + 1), knowsType);

loader.Commit();
```

---

## 6. SourceGenerator で型安全 CRUD

ボイラープレートを削減し、リファクタリング耐性を上げる。

> `[Indexed]` は SourceGenerator に `InsertIndexed` / `FindByName` および属性情報からインデックスを作成する `EnsureIndexes` / `CreateIndex` の生成を指示するマーカー。実体インデックスは書き込みトランザクションの `EditSchema.EnsureIndexes<T>()` または `EditSchema.CreateIndex<T>(p => p.Prop)` で作成する。

```csharp
[Vertex]
public partial class Person
{
    [Indexed]   // SourceGen マーカー — 実体インデックスは下で作成
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

// 初期化時に一度だけ。
using (var schemaTx = db.BeginWriteTransaction())
{
    schemaTx.EditSchema.EnsureIndexes<Person>();
    schemaTx.Commit();
}

using var tx = db.BeginWriteTransaction();
var g = tx.Query;

var id = tx.Mutate.InsertIndexed(new Person { Name = "Alice", Age = 30 });
var loaded = g.Load<Person>(id);
loaded.Age = 31;
tx.Mutate.Update(id, loaded);

var found = Person.FindByName(tx, "Alice");
```

---

## 7. WAL クラッシュリカバリの確認

WAL の PageImage replay によりコミット済みデータはクラッシュ後も完全復元される。

```csharp
VertexId savedId;

// 書き込み
using (var db = QuiverDatabase.Open(dir))
using (var tx = db.BeginWriteTransaction())
{
    savedId = tx.CreateVertex("Config");
    tx.SetProperty(savedId, "version", PropertyValue.FromString("1.0"));
    tx.Commit();
}

// 再オープン: コミット済みデータは復元される
using (var db = QuiverDatabase.Open(dir))
using (var tx = db.BeginWriteTransaction())
{
    System.Diagnostics.Debug.Assert(tx.VertexExists(savedId));
}
```

---

## 8. 統計取得と整合性チェック

運用観測のエントリ。

```csharp
var stats = db.Diagnostics.GetStatistics();
Console.WriteLine($"Vertices={stats.VertexCount}, Edges={stats.EdgeCount}");
Console.WriteLine($"BufferPool ヒット率 = {stats.BufferPoolHits} / {stats.BufferPoolHits + stats.BufferPoolMisses}");

var report = db.Diagnostics.CheckConsistency();
if (!report.IsConsistent)
{
    foreach (var issue in report.Issues)
        Console.WriteLine($"  問題: {issue}");
}
```

---

## 9. dotnet-counters でリアルタイム観測

`Quiver-EventSource` は追加 NuGet 不要で公開される `EventSource`。
別ターミナルから `dotnet-counters` を当てるだけで、buffer-pool、WAL、トランザクション、
ロック、索引、vacuum の主要メトリクスを 1 秒粒度で観測できる。

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
| `current-checkpoint-threshold-bytes` | gauge | Fixed / Adaptive 現在値 |
| `active-tx-count` | gauge | アクティブな transaction 数 |
| `tx-commit-per-sec` | rate | コミットスループット |
| `tx-abort-per-sec` | rate | アボートスループット |
| `writer-wait-duration-ms` | gauge | writer lease 取得待ち時間の累計 (ms) |
| `writer-contention-count` | gauge | writer lease の競合を観測した累計回数 |
| `active-snapshot-count` | gauge | active な reader snapshot 数 |
| `oldest-snapshot-age-seconds` | gauge | 最古 reader snapshot の経過時間 |
| `maintenance-rebuild-active` | gauge | 実行中の derived index rebuild 数 |
| `maintenance-gc-active` | gauge | 実行中の garbage collection 数 |
| `index-orphan-count` | gauge | `CheckIndexConsistency()` 最新観測の orphan 件数 |
| `vacuum-progress-percent` | gauge | vacuum 実行中の進捗 (0 = 非実行) |
| `crash-recovery-count` | rate | crash recovery 起動回数 (通常 0) |

`Quiver-EventSource` を有効化しない限り PollingCounter は生成されないので、
本機能の overhead は実質ゼロ。OpenTelemetry 経由でメトリクスを送りたい場合は
`Quiver.OpenTelemetry` パッケージの `AddQuiverInstrumentation()` を使う。

---

## 10. ローカル RAG (Quiver.Rag)

別アセンブリ `Quiver.Rag` は、文書からチャンクへの格納、取込/再取込、ハイブリッド検索、
graph expansion の定型を 1 API で提供する。エンジン本体 (`Quiver`) のみに依存し、埋め込み生成は
呼び出し側が `IChunkEmbedder` を注入する。サンプルは
[`samples/Quiver.Samples.Rag`](../samples/Quiver.Samples.Rag/)。

### 埋め込み器 (`IChunkEmbedder`) の用意

**使う埋め込みモデル (OpenAI API / ローカル ONNX 等) に対して `IChunkEmbedder` を直接実装する**
（バッチ texts → `float[][]` を返すだけ）。埋め込み生成はエンジン外原則で、RAG はこの契約を呼ぶだけ。

```csharp
sealed class MyEmbedder(MyModel model) : IChunkEmbedder
{
    // model、版、量子化、task 設定が変わったら別 ID にする。
    public string ProfileId => "acme-embed-ja-v3-fp16-retrieval";
    public int Dimensions => 768;
    public async ValueTask<float[][]> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
        => await model.EmbedBatchAsync(texts, ct);   // お使いの埋め込み呼び出し
}
```

```csharp
using Quiver;
using Quiver.Rag;

using var db = QuiverDatabase.Open("rag.quiver");

// 索引 (sourceId / ベクトル / 全文) はコンストラクタで冪等作成される。
var store = new RagStore(db, new RagStoreOptions
{
    EmbeddingDimensions = embedder.Dimensions,            // 注入する埋め込み器と一致させる
    IngestionProfile = new RagIngestionProfile
    {
        EmbeddingProfileId = embedder.ProfileId,
        NormalizationProfileId = "pdftools-nfkc-whitespace-v2",
    },
    Chunking = new ChunkingOptions { TargetSize = 800, Overlap = 100 },
    // 低選択率で頻繁に使う metadata key だけを昇格する。
    MetadataIndexes =
    [
        new RagMetadataIndex("category", "ragMetadata.category", "idx_rag_metadata_category"),
    ],
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
    })
{
    ContentRevision = "etag-2026-08-03",
};

var result = await store.UpsertDocumentAsync(doc, embedder);
// result.Disposition で no-op、属性だけの更新、本文差し替えを区別できる。
// 本文変更なら result.ReplacedDocumentVertexId が旧 ID、
// result.DocumentVertexId が新 ID。必要な利用者関係だけを明示的に再アンカーする。

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
{
    Console.WriteLine(
        $"#{h.Rank} {h.Score.FusionMethod} score={h.Score.FusedScore:F6} " +
        $"bm25={h.Score.Bm25Score} vector={h.Score.VectorSimilarity}");
    Console.WriteLine($"〈{h.Document.Title}〉{h.HeadingPath}: {h.ChunkText}");
}

// 文書差し替え (内容変更時) と削除。どちらも単一トランザクションで原子的。
UpsertResult replaced = await store.UpsertDocumentAsync(updatedDoc, embedder);
// 旧 Document、旧チャンク、旧 ID に接続した Edge と参加 Nexus は cascade 済み。
store.DeleteDocument("docs/intro.md");
```

> 取込側 (PdfTools 等) が `IngestedDocument` を JSON でやり取りする場合は、契約の正本
> `IngestedDocumentJson.Serialize` / `.Deserialize` を参照する (camelCase / `BlockKind` は文字列 /
> null フィールド省略、source-gen で NativeAOT 安全)。境界の JSON 規約が両側で一致し続ける。

**運用上の注意:**

- **再取込は `ingestionFingerprint` で判定する。** profile、Blocks の `contentHash`、`Title`、キー順を
  正規化した `Metadata`、任意の `ContentRevision` がすべて同じなら no-op。Blocks が同じで表示属性だけが
  変わった場合は `AttributesUpdated` となり、同じ Document ID と Chunk / vector を保って属性だけを更新する。
- **取込 profile はコーパス単位で固定する。** chunking、embedding model、normalization、embedding input
  template が変わると constructor または upsert が `RagIngestionProfileMismatchException` で拒否する。
  変更時は別 DB へ全 source を再取込し、検証後に利用側の `RagStore` を切り替える。
- **差し替え・削除は単一トランザクション。** 取込中にプロセスが落ちても「旧版が無傷」か
  「新版が完全」のどちらかで、中間状態は残らない。クラッシュ安全性はこの原子性に委ねている。
- **内容変更は Document ID を維持しない。** `UpsertResult` は `ReplacedDocumentVertexId` と
  `DocumentVertexId` の対応を返す。旧 ID に接続した利用者 Edge と参加 Nexus は cascade 削除され、
  新 ID へ暗黙継承されない。呼び出し側は返却された対応から、所有する関係だけを明示的に再アンカーする。
- **`RagHit.Score` は検索経路の内訳を返す。** `Bm25Score`、`VectorSimilarity`、
  `FusedScore`、`FusionMethod`、`ReciprocalRankConstant` を使い、片方のチャンネルを使わない場合は
  対応する生 score が `null` になる。
- **埋め込みはトランザクションの外で先に実行される。** `IChunkEmbedder` が失敗しても DB は無変更。
  `Dimensions` と `ProfileId` は `RagStoreOptions` の構成と一致している必要がある。
- **見出し語は BM25 でも引ける。** 見出しパスを前置した `searchText` を全文索引の対象にしているため、
  本文に出てこない見出しの語も検索でヒットする (`text` プロパティは原文スライスのまま保たれる)。
- **メタデータの絞り込みは 2 系統。** 等値条件は `MetadataEquals` (検索前に母集団を絞る candidate-side
  push-down → recall hole が起きない)。範囲条件など複雑な述語は `MetadataFilter` (後段フィルタ。上位 K 件
  取得後に除外するため、フィルタが大半を弾くと該当文書があっても 0 件になり得る)。`MetadataEquals` は
  既定で Document scan を使い、`MetadataIndexes` に昇格した string key だけ scalar index seek を使う。
- **頻繁な再取込でも ANN グラフを in-place 更新しない。** 削除と上書きは flat delta へ記録し、
  削除が累積すると自動再構築する。長期間きわめて高頻度の churn を続ける場合のみ手動再構築を検討する。
- 全文索引が未作成の場合 `RagStore.FullTextEnabled == false` となり、
  検索は KNN のみで動作する。

---

## 11. 工夫された読み取りクエリ

`Coalesce` / `Optional` / `Union` / `As` + `Select` を組み合わせた典型パターン。
実行可能なサンプルは [`samples/Quiver.Samples.QueryPatterns`](../samples/Quiver.Samples.QueryPatterns/)。

### Coalesce — 最初にマッチした分岐だけ

```csharp
// KNOWS 先があればその先を、無ければ自分自身を返す。
var names = g.Vertices().HasLabel("Person")
    .Coalesce(s => s.Out("KNOWS"), s => s)
    .Values("name").ToList();
```

### Optional — マッチしなければ元のまま

```csharp
// Cypher の OPTIONAL MATCH 相当。Out("KNOWS") が空なら元Vertexをそのまま通す。
var names = g.Vertices().HasLabel("Person")
    .Optional(s => s.Out("KNOWS"))
    .Dedup().Values("name").ToList();
```

### Union — 複数分岐をすべて連結

```csharp
// KNOWS 先と USE 先を両方放出する。
var names = g.Vertices().HasLabel("Person").Has("name", "Alice")
    .Union(s => s.Out("KNOWS"), s => s.Out("USE"))
    .Values("name").ToList();
```

### As / Select — タプル射影

```csharp
// (Person)→KNOWS→(Person) のペアを射影で取り出す。
var pairs = g.Vertices().HasLabel("Person").As("src")
    .Out("KNOWS").As("dst")
    .Select(t => (
        Src: Encoding.UTF8.GetString(tx.GetProperty(t.Vertex("src"), "name").Utf8StringValue),
        Dst: Encoding.UTF8.GetString(tx.GetProperty(t.Vertex("dst"), "name").Utf8StringValue)));
```

### 型安全 Where (式ツリー)

```csharp
// LINQ ライクな式ツリー。&&、比較、StartsWith/EndsWith/Contains に対応。
var result = g.Vertices<Person>()
    .Where(p => p.Age > 26 && p.Name.StartsWith("C"))
    .ToList();
```

---

## 12. 型安全な集合 write シンク (AddEdge / MergeEdge)

`TypedGraphTraversal<TSource>` の拡張メソッドで、始点集合と終点集合の直積に対して
辺を一括生成 / upsert する。端点の型整合は `IGraphEdge<TRel,TSource,TTarget>`
制約でコンパイル時に強制される。

> **Coalesce / Optional ブランチ内での変異 (upsert) は非対応。**
> Quiver のブランチは読み取り専用で、`fold` / `unfold` / `constant` も非対応のため、
> Gremlin の `coalesce(V().has(...), addV(...))` パターンは成立しない。
> 代替として `MergeVertex` / `MergeEdge` + C# `if` を使う (§2 / §6 参照)。

### AddEdge — 直積で常に辺を生成

```csharp
// B で始まる Person × C で始まる Tool に Use 辺を張る (プロパティ付き)。
long n = g.Vertices<Person>().Where(p => p.Name.StartsWith("B"))
    .AddEdge(g.Vertices<Tool>().Where(t => t.Name.StartsWith("C")),
             (p, t) => new Use { Note = $"{p.Name}→{t.Name}" });
// → Bob × {Cutter, Compiler} = 2 本
```

プロパティ無し版は SourceGen 糖衣で型引数を省ける:

```csharp
long n = g.Vertices<Person>().AddUse(g.Vertices<Tool>());
```

### MergeEdge — 冪等 upsert

```csharp
// 2 回目は全て既存ヒット (Matched)。プロパティは ON CREATE のみ書かれる。
var (created, matched) = g.Vertices<Person>()
    .MergeEdge(g.Vertices<Tool>(),
               (p, t) => new Use { Note = "auto" });
```

### 相関版 — 始点ごとに終点を決める

```csharp
// 各 Person の頭文字で始まる Tool だけに辺を張る。
long n = g.Vertices<Person>()
    .AddEdge(p => g.Vertices<Tool>().Where(t => t.Name.StartsWith(p.Name[..1])),
             (p, t) => new Use { Note = p.Name });
```

---

## 13. ユーザー定義 DSL (ドメイン固有トラバーサル)

拡張メソッドで既存のトラバーサルステップを合成し、ドメイン固有の語彙でクエリを書けるようにする。
SourceGenerator が生成する型保存ホップ糖衣 (§6) とシームレスに混在させられる。

### スキーマ定義 (前提)

```csharp
[Vertex]
public partial class Person
{
    [Property] public string Name { get; set; } = "";
    [Property] public int Age { get; set; }
    [Property] public string Role { get; set; } = "";
}

[Vertex]
public partial class Post
{
    [Property] public string Title { get; set; } = "";
    [Property] public long CreatedAt { get; set; }
    [Property] public bool Featured { get; set; }
}

[Edge<Person, Post>("WROTE")]
public partial class Wrote
{
    [Property] public string Note { get; set; } = "";
}
```

SourceGenerator は `Wrote` から以下の糖衣を自動生成する:

```csharp
// 自動生成 (Wrote.GraphRel.g.cs)
public static class WroteTraversalExtensions
{
    public static TypedGraphTraversal<Post> Wrote(this TypedGraphTraversal<Person> source)
        => source.Out<Wrote, Post>();

    public static TypedGraphTraversal<Post> Wrote(this TypedGraphTraversal<Person> source,
        Expression<Func<Wrote, bool>> edgeFilter)
        => source.OutWhere<Wrote, Post>(edgeFilter);

    // AddWrote, MergeWrote ...
}
```

### DSL の定義

`TypedGraphTraversal<T>` への拡張メソッドで型保存、`GraphTraversalSource` への拡張メソッドで起点を追加する。

```csharp
public static class BlogDsl
{
    // ── 起点 ──
    public static TypedGraphTraversal<Person> Authors(this GraphTraversalSource g)
        => g.Vertices<Person>().Where(p => p.Role == "author");

    // ── Person フィルタ ──
    public static TypedGraphTraversal<Person> Adults(this TypedGraphTraversal<Person> t)
        => t.Where(p => p.Age >= 18);

    // ── Post フィルタ ──
    public static TypedGraphTraversal<Post> Featured(this TypedGraphTraversal<Post> t)
        => t.Has(p => p.Featured, true);

    public static TypedGraphTraversal<Post> Since(this TypedGraphTraversal<Post> t, long unixSeconds)
        => t.Has(p => p.CreatedAt, P.Gte(unixSeconds));
}
```

### SourceGenerator 糖衣との混在チェーン

ユーザー定義 DSL も SourceGenerator 生成の糖衣も同じ `TypedGraphTraversal<T>` 上の拡張メソッドなので、自由に混ぜて書ける。

```csharp
// SourceGenerator: .Wrote()
// ユーザー DSL:    .Authors(), .Adults(), .Featured(), .Since()
var hits = g.Authors()
    .Adults()
    .Wrote()
    .Featured()
    .Since(cutoff)
    .ToList();   // List<Post>

// エッジ述語付き生成メソッドとの組み合わせ
var drafts = g.Vertices<Person>()
    .Adults()
    .Wrote(e => e.Note.Contains("draft"))
    .Since(cutoff)
    .ToList();
```

### 型なしトラバーサルとの境界

`TypedGraphTraversal<T>` から `.Out(string)` 等で `GraphTraversal<VertexId>` に降格すると、`TypedGraphTraversal<T>` 用の DSL メソッドは使えなくなる。型付きホップで辿れるなら常にそちらを使う。

```csharp
// NG: .Out("WROTE") は GraphTraversal<VertexId> を返すため .Featured() が見えない
g.Vertices<Person>().Adults()
    .Out("WROTE")    // → GraphTraversal<VertexId>
    .Featured();     // ❌ コンパイルエラー

// OK: SourceGenerator の .Wrote() は TypedGraphTraversal<Post> を返す
g.Vertices<Person>().Adults()
    .Wrote()         // → TypedGraphTraversal<Post>
    .Featured();     // ✅
```

`GraphTraversal<VertexId>` (型なし) 用の DSL を書くこともできるが、ドメイン固有の型安全性は失われる:

```csharp
// 型なし DSL も定義可能 (ラベル名の文字列指定)
public static class UntypedBlogDsl
{
    public static GraphTraversal<VertexId> People(this GraphTraversalSource g)
        => g.Vertices().HasLabel("Person");

    public static GraphTraversal<VertexId> Adults(this GraphTraversal<VertexId> t)
        => t.Has("Age", P.Gte(18L));
}

// 型なし DSL 同士のチェーンは問題ない
var names = g.People().Adults().Out("WROTE").Values("Title").ToList();
```

### MergeEdge のコスト

`MergeEdge` の存在判定は始点Vertexの同一型 outgoing edge を線形スキャン
する (**O(out-degree)**)。実測で **~116 ns/edge** の勾配 + ~2.5µs の固定コスト。

| 同一型 out-degree | 1 回の hit | 1 回の miss (scan + create) |
|---:|---:|---:|
| 10 | ~2µs | ~18µs |
| 100 | ~12µs | ~28µs |
| 1,000 | ~116µs | ~136µs |

低 fan-out (degree < 50) では 1 回数µs で実用上問題にならない。
**degree 1,000 を超える高 fan-out Vertexで大量の直積 MergeEdge を回す場合は
コストが顕在化する** (10×10 直積 × degree 1,000 ≈ 12ms)。その場合は
`AddEdge` (存在チェックなし、~6µs/call で degree 非依存) を使うか、
アプリ層で重複制御すること。エッジ存在インデックスは現時点で非目標。
詳細は[MergeEdgeのdegree依存コスト](benchmark-results.md#mergeedgeのdegree依存コスト)を参照。

---

## 14. 同期カーソルのキャンセル

```csharp
using var cursor = traversal.AsCursor();
while (cursor.MoveNext())
{
    cancellationToken.ThrowIfCancellationRequested();
    Process(cursor.Current);
}
```

長い走査をキャンセル可能にする場合は、`AsCursor()` で同期カーソルを取得し、各反復で
`CancellationToken.ThrowIfCancellationRequested()` を呼ぶ。カーソルは `using` で必ず破棄する。
同じトランザクションと列挙子を複数の操作フローから同時に使わない。


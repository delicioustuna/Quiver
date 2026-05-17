# Quiver Cookbook

Quiver の典型ユースケースをすぐに動かせるレシピ集。各レシピは [`samples/`](../samples/) のいずれかと対応している。

---

## 1. 重み付き shortest-path

エッジに重みプロパティを乗せ、`ShortestPathTo` でホップ数最短経路を求める。

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

// ホップ数最短 (本記事範囲: 重み考慮の Dijkstra は将来対応)
var hops = g.Node(s).ShortestPathTo(t, type: "ROAD").TryNext();
Console.WriteLine($"S→T 最短ホップ数: {hops}");
```

> 注: 現状の `ShortestPathTo` はホップ数最短のみ対応。重み付き Dijkstra/A* は PW-13 GraphKernel の上に将来実装する想定。

---

## 2. MERGE で upsert

冪等な書き込みパターン。同じキーで何度実行しても重複ノードを増やさない。

```csharp
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

```csharp
[GraphNode]
public partial class Person
{
    [GraphIndexed]
    [GraphProperty]
    public string Name { get; set; } = "";

    [GraphProperty]
    public int Age { get; set; }
}

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

# 05. KNN ベクトル検索

VEC-5 / VEC-6 のベクトル検索とグラフトラバーサルを結合する。完全コードは [`samples/Quiver.Samples.Vector`](https://github.com/anthropics/quiver/tree/main/samples/Quiver.Samples.Vector)。

```csharp
using var db = GraphDatabase.Open("./mygraph");

// インデックス作成
db.Vectors.CreateIndex(
    "person_bio_v1",
    new VectorIndexSpec(Dimensions: 4, Metric: VectorMetric.Cosine, EntityKind: EntityKind.Node));

// データ登録
using (var tx = db.BeginTransaction())
{
    var g = tx.G(db.Schema);
    var alice = g.AddNode("Person").P("Name", "Alice").Next();
    var bob   = g.AddNode("Person").P("Name", "Bob").Next();

    db.Vectors.SetVector("person_bio_v1", EntityId.FromNode(alice), new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
    db.Vectors.SetVector("person_bio_v1", EntityId.FromNode(bob),   new float[] { 0.0f, 0.1f, 0.2f, 0.5f });
    tx.Commit();
}

// KNN を起点としたトラバーサル (VEC-5)
using (var tx = db.BeginReadOnlyTransaction())
{
    var g = tx.G(db.Schema);
    var query = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };

    var top2Names = g.Knn("person_bio_v1", query, k: 2)
                     .Values("Name")
                     .ToList();
}

// graph-first ハイブリッド (VEC-6)
using (var tx = db.BeginReadOnlyTransaction())
{
    var g = tx.G(db.Schema);
    var query = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };

    var filtered = g.Nodes().HasLabel("Person")
                    .Has("Name", "Alice")
                    .FilterByKnn("person_bio_v1", query, k: 1)
                    .Values("Name")
                    .ToList();
}
```

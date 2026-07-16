# 05. KNN ベクトル検索

ベクトル検索とグラフトラバーサルを結合する。
完全コードは [`samples/Quiver.Samples.Vector`](https://github.com/delicioustuna/Quiver/tree/main/samples/Quiver.Samples.Vector)。

```csharp
using var db = QuiverDatabase.Open("./mygraph");

// インデックス作成
db.Vectors.CreateIndex(
    "person_bio_v1",
    new VectorIndexSpec(Dimensions: 4, Metric: VectorMetric.Cosine, EntityKind: EntityKind.Vertex));

// データ登録
using (var tx = db.BeginTransaction())
{
    var g = tx.G(db.Schema);
    var alice = g.AddVertex("Person").P("Name", "Alice").Next();
    var bob   = g.AddVertex("Person").P("Name", "Bob").Next();

    db.Vectors.SetVector("person_bio_v1", EntityId.FromVertex(alice), new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
    db.Vectors.SetVector("person_bio_v1", EntityId.FromVertex(bob),   new float[] { 0.0f, 0.1f, 0.2f, 0.5f });
    tx.Commit();
}

// KNN を起点としたトラバーサル
using (var tx = db.BeginReadOnlyTransaction())
{
    var g = tx.G(db.Schema);
    var query = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };

    var top2Names = g.Knn("person_bio_v1", query, k: 2)
                     .Values("Name")
                     .ToList();
}

// graph-first ハイブリッド
using (var tx = db.BeginReadOnlyTransaction())
{
    var g = tx.G(db.Schema);
    var query = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };

    var filtered = g.Vertices().HasLabel("Person")
                    .Has("Name", "Alice")
                    .FilterByKnn("person_bio_v1", query, k: 1)
                    .Values("Name")
                    .ToList();
}
```

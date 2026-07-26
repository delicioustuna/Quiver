# 05. KNN ベクトル検索

ベクトル検索とグラフトラバーサルを同じ transaction snapshot で実行する。
完全な例は [`samples/Quiver.Samples.Vector`](https://github.com/delicioustuna/Quiver/tree/main/samples/Quiver.Samples.Vector) にある。

```csharp
using var db = QuiverDatabase.Open("./mygraph");

using (var schema = db.BeginWriteTransaction())
{
    schema.EditSchema.CreateIndex(new VectorIndexDefinition(
        "person_bio_v1",
        new PropertyTarget(
            PropertyOwnerKind.Vertex,
            "bio_embedding",
            "Person"),
        Dimensions: 4,
        Metric: DistanceMetric.Cosine));
    schema.Commit();
}

using (var write = db.BeginWriteTransaction())
{
    VertexId alice = write.CreateVertex("Person");
    VertexId bob = write.CreateVertex("Person");
    write.SetProperty(alice, "Name", "Alice");
    write.SetProperty(bob, "Name", "Bob");
    write.SetVectorProperty(
        EntityRef.From(alice),
        "bio_embedding",
        [0.1f, 0.2f, 0.3f, 0.4f]);
    write.SetVectorProperty(
        EntityRef.From(bob),
        "bio_embedding",
        [0.0f, 0.1f, 0.2f, 0.5f]);
    write.Commit();
}

using (var read = db.BeginReadTransaction())
{
    float[] query = [0.1f, 0.2f, 0.3f, 0.4f];
    List<string> top2Names = read.Query
        .Knn("person_bio_v1", query, k: 2)
        .Values("Name")
        .ToList();
}

using (var read = db.BeginReadTransaction())
{
    float[] query = [0.1f, 0.2f, 0.3f, 0.4f];
    List<string> filtered = read.Query
        .Vertices()
        .HasLabel("Person")
        .Has("Name", "Alice")
        .FilterByKnn("person_bio_v1", query, k: 1)
        .Values("Name")
        .ToList();
}
```

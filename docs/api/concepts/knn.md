# KNN ベクトル検索

Yatagarasu は vector を owner-bound property として保存し、read transaction の snapshot で KNN を実行する。

## インデックスの作成

```csharp
using var schema = db.BeginWriteTransaction();
schema.EditSchema.CreateIndex(new VectorIndexDefinition(
    "person_bio_v1",
    new PropertyTarget(
        PropertyOwnerKind.Vertex,
        "bio_embedding",
        "Person"),
    Dimensions: 384,
    Metric: DistanceMetric.Cosine));
schema.Commit();
```

## ベクトルの保存

```csharp
using var write = db.BeginWriteTransaction();
write.SetVectorProperty(
    EntityRef.From(personId),
    "bio_embedding",
    embeddingArray);
write.Commit();
```

vector property はインデックスの有無に依存しない。
index の drop と rebuild は primary value を削除しない。

## スコア付き KNN

```csharp
using var read = db.BeginReadTransaction();
using VectorSearchCursor hits = read.KnnSearch(
    "person_bio_v1",
    queryVector,
    k: 10);

while (hits.MoveNext())
{
    Console.WriteLine($"{hits.Current.Owner}: {hits.Current.Score}");
}
```

## トラバーサルとの結合

```csharp
var top10Friends = read.Query
    .Knn("person_bio_v1", queryVector, k: 10)
    .Out("KNOWS")
    .Has("active", true)
    .ToList();
```

graph-first の絞り込みは typed owner identity を保持し、KNN candidate を primary property で再検証する。

```csharp
var candidates = read.Query
    .Vertices()
    .HasLabel("Person")
    .Has("region", "JP")
    .FilterByKnn("person_bio_v1", queryVector, k: 50)
    .ToList();
```

埋め込みの生成は利用者のアプリケーションが担う。
保存先は index 名ではなく vector property key で指定する。

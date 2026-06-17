# KNN (Vector Search)

Quiver はベクトルストア (`IVectorStore`) を介して KNN 検索をサポートし、グラフトラバーサルとシームレスに結合できる。

## ベクトルインデックス作成

```csharp
db.Vectors.CreateIndex(
    name: "person_bio_v1",
    spec: new VectorIndexSpec(
        Dimensions: 384,
        Metric: VectorMetric.Cosine,
        EntityKind: EntityKind.Node));
```

## ベクトル登録

```csharp
db.Vectors.SetVector(
    indexName: "person_bio_v1",
    entityId: EntityId.FromNode(personId),
    vector: embeddingArray);
```

## KNN 検索 (生スコア付き)

```csharp
var hits = db.Vectors.KnnSearch(
    indexName: "person_bio_v1",
    query: queryVector,
    k: 10);

foreach (var hit in hits)
    Console.WriteLine($"{hit.EntityId}: {hit.Score}");
```

## トラバーサルとの結合 (VEC-5)

KNN スキャンをトラバーサル起点にする:

```csharp
var top10Friends = g.Knn("person_bio_v1", queryVec, k: 10)
                    .Out("KNOWS")
                    .Has("active", true)
                    .ToList();
```

## graph-first ハイブリッド (VEC-6)

グラフフィルタを先に評価し、その結果集合に対してのみ KNN を行うパターン。

```csharp
var candidates = g.Nodes().HasLabel("Person")
                  .Has("region", "JP")
                  .FilterByKnn("person_bio_v1", queryVec, k: 50)
                  .ToList();
```

## 埋め込みパイプライン

`Quiver.Embedding`（**incubating: 現状 NuGet 非公開**。リポジトリ内アセンブリとして利用可）の `TextEmbeddingPipeline` を用いると、`OnCommitted` フックでテキストプロパティから自動的に埋め込みを生成・登録できる。

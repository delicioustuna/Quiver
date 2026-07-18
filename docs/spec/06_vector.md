# ベクトル検索

> as-built 仕様（QUIVER-SW family version 2、2026-07-18）

## Primary vector property

ベクトル値の正本は、owner に束縛された `FloatArray` property version である。
`IWriteTransaction.SetVectorProperty(owner, propertyKey, vector)` は通常の property mutation と同じトランザクションへ値を書き込む。
ベクトルインデックスが存在しない場合も、property は commit、rollback、reopen の規則に従う。

property version は配列本体ではなく、immutable な `VectorPayloadRef(Sequence, Generation)` を保持する。
payload metadata は element type、generation、dimensions、byte length、CRC32C checksum、blob ID を保持する。
読み取りは ref generation、element type、dimensions、byte length、blob length、checksum を検証する。
到達可能な property version が参照する payload の欠落または不一致だけを primary corruption とする。

`IReadTransaction.TryGetVectorProperty(owner, propertyKey, destination)` は、その read transaction の snapshot で可視な property version を読む。
destination が短い場合、owner が可視でない場合、または property が `FloatArray` でない場合は `false` を返す。

## Vector index definition

ベクトルインデックスは `VectorIndexDefinition` で宣言する。
definition は scalar index と同じ `IndexDefinition` catalog に参加し、`ISchemaEditor.CreateIndex` と `DropIndex` で変更する。

| フィールド | 説明 |
|---|---|
| `Name` | 一意なインデックス名 |
| `Target` | owner kind、vector property key、任意の label または type scope |
| `Dimensions` | 正の次元数 |
| `Metric` | `Cosine`、`Dot`、`Euclidean` |
| `ElementType` | 現在は `Float32` のみ |
| `HnswM` | 上位レイヤの最大近傍数 |
| `HnswMMax0` | レイヤ 0 の最大近傍数 |
| `HnswMaxLayers` | 最大レイヤ数 |
| `HnswEfConstruction` | artifact 構築時の探索幅 |
| `SegmentPolicy` | delta entry 数と segment 数の merge しきい値 |

definition catalog は target property key と scope を明示的に保存する。
embedding 元 property、provider、normalization profile は index definition に含めず、Embedding task metadata が保持する。

```csharp
using var schema = database.BeginWriteTransaction();
schema.EditSchema.CreateIndex(new VectorIndexDefinition(
    "document_embedding",
    new PropertyTarget(
        PropertyOwnerKind.Vertex,
        "embedding",
        "Document"),
    Dimensions: 384,
    Metric: DistanceMetric.Cosine));
schema.Commit();
```

index を drop しても primary vector property と payload は削除しない。
同じ target で index を作り直すと、検索経路は primary property から derived segment を再構築できる。

## Immutable vector segments

vector property の commit は、一致する definition ごとに commit-local flat delta segment を公開する。
segment entry は full typed owner identity と payload checksum を保持する。
mutable な共有 HNSW へ commit ごとに insert する経路はない。

検索 manifest は `xmin` と `xmax` を持つ versioned state である。
old reader は開始時点で可視だった manifest を使い続け、新しい reader だけが publish 後の manifest を使う。
検索は可視な flat segment と immutable HNSW segmentを fan-out し、segment ごとの候補を top-k heap で統合する。

merge worker は committed primary property の read snapshot を取得し、writer lease の外で HNSW artifact を構築する。
publish 用の短い write transaction は source manifest generation と現在の definition を再検証する。
どちらかが変わっていれば artifact を破棄し、新しい snapshot から再試行する。

derived segment と manifest は primary value の正本ではない。
reopen 後や derived state が不足する場合、KNN は同じ read snapshot の primary property を exact scan し、バックグラウンド rebuild を要求する。
そのため、derived state の欠落は committed vector property の消失や database open の失敗を意味しない。

## Candidate validation

segment candidate は logical result に変換する前に primary store で再検証する。
検証対象は full typed owner identity、owner generation、owner の snapshot visibility、target scope、property key、dimensions、payload checksum である。
削除済み owner、同じ sequence の別 generation、更新前 property、別 target の entry は結果から除外する。

raw sequence は physical store の read 成功直後にだけ使う。
transaction、query、traversal、検索結果は `EntityRef`、`VertexId`、`EdgeId`、`NexusId` の full identity を保持する。

## Transaction-scoped KNN

`IReadTransaction.KnnSearch` と `KnnSearchBatch` は、transaction snapshot から definition、manifest、primary property を解決する。
cursor は transaction の利用期間を超えて使えない。

```csharp
using var read = database.BeginReadTransaction();
using VectorSearchCursor cursor = read.KnnSearch(
    "document_embedding",
    queryVector,
    k: 10);

while (cursor.MoveNext())
{
    EntityRef owner = cursor.Current.Owner;
    float score = cursor.Current.Score;
}
```

すべての metric は「スコアが大きいほど近い」という規約にそろえる。
`Dot` は内積、`Cosine` は cosine similarity、`Euclidean` は二乗距離の符号反転を返す。

## Embedding と RAG

`Quiver.Embedding` は source property と vector target property を別々の task metadata として扱う。
pipeline は embedding を生成した後、write transaction の `SetVectorProperty` で target property を保存する。

`Quiver.Rag` は read transaction の `KnnSearch` と graph property read を同じ snapshot で実行する。
database または backend から vector store を取得する公開 API は存在しない。

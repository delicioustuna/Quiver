# Vertex / Edge モデル

Quiver は有向プロパティグラフを採用する。Vertexはラベルを 1 つ持ち、複数のプロパティをキーバリュー形式で保持する。Edgeは型を 1 つ持ち、両端Vertex (`Source` / `Target`) と複数のプロパティを保持する。

## ID 型

すべての識別子は `readonly record struct` で型安全に表現する。

```csharp
public readonly record struct VertexId(long Value);
public readonly record struct EdgeId(long Value);
public readonly record struct NexusId(long Value);
public readonly record struct LabelId(int Value);
public readonly record struct EdgeTypeId(int Value);
public readonly record struct PropertyKeyId(int Value);
public readonly record struct TransactionId(long Value);
```

Vertex、Edge、Nexus の ID は generation と sequence を含む。
Property は owner と property key に束縛された versioned value であり、公開 ID を持たない。

## プロパティ値の型

| 型 | C# 表現 |
|---|---|
| `Bool` | `bool` |
| `Int32` | `int` |
| `Int64` | `long` |
| `Double` | `double` |
| `String` | UTF-8 バイト列 |
| `Bytes` | 任意バイト列 |

```csharp
tx.SetProperty(vertexId, "age",  PropertyValue.FromInt32(30));
tx.SetProperty(vertexId, "name", PropertyValue.FromString("Alice"));
tx.SetProperty(vertexId, "score", PropertyValue.FromDouble(95.5));
```

## ラベルとEdge型

ラベル名・Edge型名・プロパティキー名は内部でトークン化され、整数 ID (`LabelId` / `EdgeTypeId` / `PropertyKeyId`) で表現される。
読み取り時のトークン解決は `ISchemaCatalog`、作成と変更は書き込みトランザクションの `ISchemaEditor` 経由で行う。

```csharp
using var tx = db.BeginWriteTransaction();
var personLabel = tx.EditSchema.GetOrCreateLabel("Person");
var knowsType = tx.EditSchema.GetOrCreateEdgeType("KNOWS");
var nameKey = tx.EditSchema.GetOrCreatePropertyKey("name");
tx.Commit();
```

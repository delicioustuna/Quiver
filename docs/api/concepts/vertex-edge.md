# Vertex / Edge モデル

Quiver は有向プロパティグラフを採用する。Vertexはラベルを 1 つ持ち、複数のプロパティをキーバリュー形式で保持する。Edgeは型を 1 つ持ち、両端Vertex (`Source` / `Target`) と複数のプロパティを保持する。

## ID 型

すべての識別子は `readonly record struct` で型安全に表現する。

```csharp
public readonly record struct VertexId(long Value);
public readonly record struct EdgeId(long Value);
public readonly record struct PropertyId(long Value);
public readonly record struct LabelId(int Value);
public readonly record struct EdgeTypeId(int Value);
public readonly record struct PropertyKeyId(int Value);
public readonly record struct TransactionId(long Value);
```

`-1` は「無効 / null」を意味する予約値。

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

ラベル名・Edge型名・プロパティキー名は内部でトークン化され、整数 ID (`LabelId` / `EdgeTypeId` / `PropertyKeyId`) で表現される。トークン解決は `ISchemaApi` 経由で行う。

```csharp
var personLabel = db.Schema.GetOrCreateLabel("Person");
var knowsType   = db.Schema.GetOrCreateEdgeType("KNOWS");
var nameKey     = db.Schema.GetOrCreatePropertyKey("name");
```

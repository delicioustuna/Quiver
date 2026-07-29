# Vertex / Edge / Nexus モデル

Quiver は有向プロパティグラフを採用する。Vertexはラベルを 1 つ持ち、複数のプロパティをキーバリュー形式で保持する。Edgeは型を 1 つ持ち、両端Vertex (`Source` / `Target`) と複数のプロパティを保持する。Nexusは型とロール付きの複数Vertexメンバーを持つn項関係であり、作成時に確定するメンバー集合と複数のプロパティを保持する。

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
| `FloatArray` | `float` の配列 |

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

## Edge / Nexus の読み取りと構造置換

Edgeの端点と型は`TryGetEdge`、全プロパティは`EnumerateProperties`で同じtransaction snapshotから読める。

```csharp
if (tx.TryGetEdge(edgeId, out var edge))
    Console.WriteLine($"{edge.Source} -[{edge.Type}]-> {edge.Target}");

var properties = tx.EnumerateProperties(edgeId);
while (properties.MoveNext())
    Console.WriteLine(properties.Current.KeyId);
```

Edgeの端点・型とNexusのメンバー・型は不変である。変更するときは置換APIを使う。
置換は全プロパティを型とcardinalityを保って移すが、IDは維持しない。

```csharp
EdgeReplacement edgeReplacement = tx.ReplaceEdge(
    oldEdgeId, newSource, newTarget, "AUTHORED");

NexusReplacement nexusReplacement = tx.ReplaceNexus(
    oldNexusId,
    "Fact",
    [new("Subject", subject), new("Object", replacementObject)]);
```

戻り値の`OldId` / `NewId`をapplication migrationの参照更新に利用する。

個別Vertexのラベルを変える場合、単純な`Insert + Delete`では旧Vertexに接続する関係がcascade削除される。
`ReplaceVertex`は新Vertexへ全propertyをコピーし、接続するEdgeとNexusを張り替えてから旧Vertexを削除する。
複数Vertexが同じ関係に参加するときは、単体APIを順番に呼ばず`ReplaceVertices`へまとめて渡す。

```csharp
VertexGraphRewriteResult mappings = tx.ReplaceVertices([
    new VertexRewriteRequest(oldPerson, "Customer"),
    new VertexRewriteRequest(oldCompany, "Organization"),
]);

VertexId newPerson = mappings.VertexMappings
    .Single(x => x.OldId == oldPerson).NewId;
```

batchは完全なVertex対応表を各Edge / Nexusへ一度だけ適用し、`VertexMappings`、`EdgeMappings`、
`NexusMappings`を返す。IDは維持されないため、アプリケーション側の外部参照もこの対応表で更新する。
構造置換は旧entityの全propertyを保存する。型付きmodelから削除またはrenameした旧keyは、置換後の
新IDに対して`RemoveProperty`を明示しない限り残る。

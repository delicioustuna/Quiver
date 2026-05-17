# 03. MERGE / UPSERT

Cypher の `MERGE` 相当を使い、重複作成を避けつつ ON CREATE / ON MATCH の分岐を書く。完全コードは [`samples/Quiver.Samples.Match`](https://github.com/anthropics/quiver/tree/main/samples/Quiver.Samples.Match)。

```csharp
using var db = GraphDatabase.Open("./mygraph");
using var tx = db.BeginTransaction();

var (id, created) = tx.MergeNode(
    "Person",
    "email",
    PropertyValue.FromString("alice@example.com"));

if (created)
{
    tx.SetProperty(id, "createdAt", PropertyValue.FromInt64(Now()));
}
else
{
    tx.SetProperty(id, "lastSeenAt", PropertyValue.FromInt64(Now()));
}

tx.Commit();
```

## Match DSL によるパターンマッチ

```csharp
var g = tx.G(db.Schema);
var pairs = g.Match(
    GraphPattern.Node("n", "Person")
                .Out("KNOWS", GraphPattern.Node("m", "Person"))
)
.Where("n", "age", P.Gt(25L))
.Return(v => new
{
    From = v["n"].Get<string>("name"),
    To   = v["m"].Get<string>("name"),
})
.ToList();
```

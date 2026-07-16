# 03. MERGE / UPSERT

Cypher の `MERGE` 相当を使い、重複作成を避けつつ ON CREATE / ON MATCH の分岐を書く。完全コードは [`samples/Quiver.Samples.Match`](https://github.com/delicioustuna/Quiver/tree/main/samples/Quiver.Samples.Match)。

```csharp
using var db = QuiverDatabase.Open("./mygraph");

// MergeVertex を高速化するため、起動時に一度だけインデックスを作成する。
// 未作成の場合はフルスキャン経路に落ち、初回呼び出しで Trace 警告が出る。
db.Schema.CreateIndex("idx_person_email", "Person", "email", IndexKind.StringEquality);

using var tx = db.BeginTransaction();

var (id, created) = tx.MergeVertex(
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
    GraphPattern.Vertex("n", "Person")
                .Out("KNOWS", GraphPattern.Vertex("m", "Person"))
)
.Where("n", "age", P.Gt(25L))
.Return(v => new
{
    From = v["n"].Get<string>("name"),
    To   = v["m"].Get<string>("name"),
})
.ToList();
```

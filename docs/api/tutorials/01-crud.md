# 01. CRUD 基本

VertexとEdgeの基本的な作成・読み出し・更新・削除を一通り体験する。

完全なコードは [`samples/Yatagarasu.Samples.Crud`](https://github.com/delicioustuna/Yatagarasu/tree/main/samples/Yatagarasu.Samples.Crud) 参照。

```csharp
using var db = YatagarasuDatabase.Open("./mygraph");
using var tx = db.BeginWriteTransaction();

// ── Create ────────────────────────────
var alice = tx.CreateVertex("Person");
var bob   = tx.CreateVertex("Person");
tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
tx.SetProperty(alice, "age",  PropertyValue.FromInt32(30));
tx.SetProperty(bob,   "name", PropertyValue.FromString("Bob"));
tx.CreateEdge(alice, bob, "KNOWS");

// ── Read ──────────────────────────────
Console.WriteLine(tx.VertexExists(alice));                  // True
Console.WriteLine(tx.GetProperty(alice, "age").Int32Value); // 30

// ── Update ────────────────────────────
tx.SetProperty(alice, "age", PropertyValue.FromInt32(31));

// ── Delete ────────────────────────────
tx.RemoveProperty(alice, "age");

tx.Commit();
```

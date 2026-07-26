# 02. 多段トラバーサル

Gremlin 風の DSL で多段トラバーサルを行う。完全コードは [`samples/Quiver.Samples.Traversal`](https://github.com/delicioustuna/Quiver/tree/main/samples/Quiver.Samples.Traversal)。

```csharp
using var tx = db.BeginReadTransaction();
var g = tx.Query;

// 25 歳より上の Person を年齢降順で 10 件
var top10 = g.Vertices().HasLabel("Person")
             .Has("age", P.Gt(25L))
             .OrderByDescending("age")
             .Limit(10)
             .Values("name")
             .ToList();

// Alice から KNOWS で 2 ホップで到達できる人々
var twoHopFriends = g.Vertices().HasLabel("Person")
                     .Has("name", "Alice")
                     .Repeat(s => s.Out("KNOWS"), times: 2)
                     .Dedup()
                     .Values("name")
                     .ToList();

// Alice から Bob への最短経路の長さ
var dist = g.Vertices().HasLabel("Person")
            .Has("name", "Alice")
            .ShortestPathTo(bobId, type: "KNOWS")
            .TryNext();
```

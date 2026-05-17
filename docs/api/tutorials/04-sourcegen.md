# 04. SourceGenerator で型付き CRUD

`[GraphNode]` / `[GraphProperty]` / `[GraphIndexed]` / `[GraphRelationship]` を付与すると、Roslyn SourceGenerator が型安全な CRUD メソッドを自動生成する。完全コードは [`samples/Quiver.Samples.SourceGen`](https://github.com/anthropics/quiver/tree/main/samples/Quiver.Samples.SourceGen)。

```csharp
[GraphNode]
public partial class Person
{
    [GraphIndexed]
    [GraphProperty]
    public string Name { get; set; } = "";

    [GraphProperty]
    public int Age { get; set; }
}
```

> `[GraphIndexed]` は **SourceGenerator に対するマーカー** で、`InsertIndexed` / `FindBy{Prop}` の生成をトリガするだけ。**実行時の B+Tree インデックス自体はユーザが `Schema.CreateIndex` で明示的に作成する必要がある**。属性に書いたインデックス名 (省略時 `idx_{label}_{propertyName}`) と `CreateIndex` の `indexName` 引数を一致させること。`MergeNode` も同じ `(label, propertyKey)` のインデックスを自動で利用するので、業務キー upsert を使う場合も `CreateIndex` は必須。

```csharp
using var db = GraphDatabase.Open("./mygraph");
// [GraphIndexed("idx_person_name")] と一致する名前で実体インデックスを作成する。
db.Schema.CreateIndex("idx_person_name", "Person", "Name", IndexKind.StringEquality);

using var tx = db.BeginTransaction();
var g = tx.G(db.Schema);

// 型安全 Insert / Load / Update / Delete
var aliceId = g.InsertIndexed(new Person { Name = "Alice", Age = 30 });
var alice = g.Load<Person>(aliceId);
alice.Age = 31;
g.Update(aliceId, alice);

// SourceGenerator 生成の FindByName
var found = Person.FindByName(tx, "Alice");
Console.WriteLine($"{found.Count} 件, age = {found[0].Entity.Age}");

g.Delete<Person>(aliceId);
tx.Commit();
```

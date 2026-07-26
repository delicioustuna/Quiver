# 04. SourceGenerator で型付き CRUD

`[Vertex]` / `[Property]` / `[Indexed]` / `[Edge]` を付与すると、Roslyn SourceGenerator が型安全な CRUD メソッドを自動生成する。完全コードは [`samples/Quiver.Samples.SourceGen`](https://github.com/delicioustuna/Quiver/tree/main/samples/Quiver.Samples.SourceGen)。

```csharp
[Vertex]
public partial class Person
{
    [Indexed]
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}
```

> `[Indexed]` は **SourceGenerator に対するマーカー** で、`InsertIndexed` / `FindBy{Prop}` の生成と、属性情報を実体インデックスに反映する `EnsureIndexes` / `CreateIndex` の生成をトリガする。実行時の B+Tree インデックス自体は起動時に明示的に作成する必要があり、推奨は SourceGenerator 由来の糖衣 API:
>
> ```csharp
> db.EnsureIndexes<Person>();                          // 属性付き全プロパティ一括
> db.CreateIndex<Person>(p => p.Name);                 // 単一プロパティ (kind は型から推論)
> db.CreateIndex<Person>(p => p.Name, IndexKind.StringRange);  // kind を上書き
> ```
>
> `EditSchema.CreateIndex(new ScalarIndexDefinition(...))` で文字列指定もできるが、属性の値と二重に書くことになるので新規コードでは上記の型付き API を推奨。`MergeVertex` も同じ `(label, propertyKey)` のインデックスを自動で利用するので、業務キー upsert を使う場合も初期化時に index 作成が必須。

```csharp
using var db = QuiverDatabase.Open("./mygraph");
using (var schemaTx = db.BeginWriteTransaction())
{
    schemaTx.EditSchema.EnsureIndexes<Person>();
    schemaTx.Commit();
}

using var tx = db.BeginWriteTransaction();
var g = tx.Query;

// 型安全 Insert / Load / Update / Delete
var aliceId = tx.Mutate.InsertIndexed(new Person { Name = "Alice", Age = 30 });
var alice = g.Load<Person>(aliceId);
alice.Age = 31;
tx.Mutate.Update(aliceId, alice);

// SourceGenerator 生成の FindByName
var found = Person.FindByName(tx, "Alice");
Console.WriteLine($"{found.Count} 件, age = {found[0].Entity.Age}");

tx.Mutate.Delete<Person>(aliceId);
tx.Commit();
```

# 04. SourceGenerator で型付き CRUD

`[Vertex]` / `[Property]` / `[Indexed]` / `[Edge]` を付与すると、Roslyn SourceGenerator が型安全な CRUD メソッドを自動生成する。完全コードは [`samples/Yatagarasu.Samples.SourceGen`](https://github.com/delicioustuna/Yatagarasu/tree/main/samples/Yatagarasu.Samples.SourceGen)。

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
using var db = YatagarasuDatabase.Open("./mygraph");
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

生成される`Update`は従来どおりプロパティだけを変更する。Vertexの個別ラベル、Edgeの端点、
またはNexusのロール束縛を
変更する場合は、生成クラスの`Replace`を使う。`Replace`は新しいIDを返し、既存の全プロパティを
移した後、渡した型付きmodelが宣言するプロパティを`Update`と同じ規則で上書きする。

```csharp
VertexGraphRewriteResult personIds = Customer.Replace(
    tx, oldPersonId, new Customer { Name = "Alice", Age = 31 });

EdgeReplacement edgeIds = Knows.Replace(
    tx, oldEdgeId, aliceId, newFriendId, new Knows { Since = 2026 });

NexusReplacement nexusIds = Fact.Replace(
    tx, oldNexusId, new Fact
    {
        Subject = new GraphVertexRef<Person>(aliceId),
        Object = new GraphVertexRef<Person>(newObjectId),
        Predicate = "likes",
    });
```

Vertexの生成`Replace`はgraph rewriteへ委譲するため、incident Edge / Nexusも新Vertexへ張り替わる。
大量のmodel移行では単体の生成`Replace`を反復せず、`ReplaceVertices`で構造をbatch置換してから
新IDに対して生成`Update`を適用する。

生成`Replace`は旧entityの全propertyを先に保存し、target modelが宣言するpropertyだけを`Update`で
上書きする。このため、target modelから削除したkeyやrename前の旧keyは自動では消えない。
application migrationでは旧modelを読み、利用者が型変換と値写像を行って新modelを組み立て、
`Replace`の戻り値に含まれる新IDへ`RemoveProperty`を明示して不要な旧keyを削除する。
外部参照も同じ新IDへ更新する。この契約はVertex、Edge、Nexusで共通である。

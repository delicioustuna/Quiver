// Quiver.Samples.SourceGen — 属性ベースの型付き CRUD と型付きトラバーサル。
//
// 実行: dotnet run --project samples/Quiver.Samples.SourceGen

using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Samples.SourceGen;

string dir = Path.Combine(Path.GetTempPath(), "quiver_sourcegen_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = GraphDatabase.Open(dir);
    // [GraphIndexed] 付き全プロパティのインデックスを SourceGenerator 生成情報から一括作成。
    db.EnsureIndexes<Person>();

    // ── 1. 型付き Insert / Load / Update / Delete ──
    using (var tx = db.BeginTransaction())
    {
        var g = tx.G(db.Schema);

        var aliceId = g.InsertIndexed(new Person { Name = "Alice", Age = 30 });
        var bobId   = g.InsertIndexed(new Person { Name = "Bob",   Age = 25 });
        Console.WriteLine($"  Insert: Alice={aliceId.Value}, Bob={bobId.Value}");

        Knows.Insert(tx, aliceId, bobId, new Knows { Since = "2024-01" });

        var loaded = g.Load<Person>(aliceId);
        Console.WriteLine($"  Load: Name={loaded.Name}, Age={loaded.Age}");

        loaded.Age = 31;
        g.Update(aliceId, loaded);
        Console.WriteLine($"  Update 後: Age={g.Load<Person>(aliceId).Age}");

        var found = Person.FindByName(tx, "Alice");
        Console.WriteLine($"  FindByName(\"Alice\"): {found.Count} 件, Age={found[0].Entity.Age}");

        tx.Commit();
    }

    // ── 2. 型付きトラバーサル: g.Nodes<Person>().Has(p => p.Age, P.Gt(20)) ──
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);

        var adults = g.Nodes<Person>()
                      .Has(p => p.Age, P.Gt(20L))
                      .ToList();
        Console.WriteLine();
        Console.WriteLine($"  type-safe traversal: {adults.Count} 件");
        foreach (var p in adults)
            Console.WriteLine($"    {p.Name}, age={p.Age}");
    }
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

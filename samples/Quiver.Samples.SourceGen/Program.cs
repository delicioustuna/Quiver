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
    using var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
    // [Indexed] 付き全プロパティのインデックスを SourceGenerator 生成情報から一括作成。
    db.EnsureIndexes<Person>();

    // ── 1. 型付き Insert / Load / Update / Delete ──
    using (var tx = db.BeginTransaction())
    {
        var g = tx.G(db.Schema);

        var aliceId = g.InsertIndexed(new Person { Name = "Alice", Age = 30, Height = 1.65f, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Tags = ["dev", "senior"] });
        var bobId   = g.InsertIndexed(new Person { Name = "Bob",   Age = 25, Height = 1.80f, CreatedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Tags = ["dev", "junior"] });
        Console.WriteLine($"  Insert: Alice={aliceId.Value}, Bob={bobId.Value}");

        Knows.Insert(tx, aliceId, bobId, new Knows { Since = "2024-01" });

        var loaded = g.Load<Person>(aliceId);
        Console.WriteLine($"  Load: Name={loaded.Name}, Age={loaded.Age}, Tags=[{string.Join(", ", loaded.Tags)}]");

        loaded.Age = 31;
        loaded.Tags = ["dev", "lead"];
        g.Update(aliceId, loaded);
        var afterUpdate = g.Load<Person>(aliceId);
        Console.WriteLine($"  Update 後: Age={afterUpdate.Age}, Tags=[{string.Join(", ", afterUpdate.Tags)}]");

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

        // ── 3. Hop 型保存トラバーサル (ARCH-8): .Knows() が TypedGraphTraversal<Person> を保つ ──
        var known = g.Nodes<Person>()
                     .Has(p => p.Name, "Alice")
                     .Knows()                 // ← Person -KNOWS-> Person、型保存のまま
                     .Has(p => p.Age, P.Lt(30L))
                     .ToList();
        Console.WriteLine($"  Alice が知る 30 歳未満: {known.Count} 件");
        foreach (var p in known)
            Console.WriteLine($"    {p.Name}, age={p.Age}");

        // ── 4. GC-7: 式ツリー述語 (.Where) — LINQ ライクなノード絞り込み ──
        var lambdaFiltered = g.Nodes<Person>()
                              .Where(p => p.Age > 20 && p.Name.StartsWith("A"))
                              .ToList();
        Console.WriteLine($"  Where(Age>20 && Name^=A): {lambdaFiltered.Count} 件");
        foreach (var p in lambdaFiltered)
            Console.WriteLine($"    {p.Name}, age={p.Age}");

        // ── FT-35: 浮動小数点 (float) の範囲述語 ──
        var tall = g.Nodes<Person>()
                    .Where(p => p.Height > 1.7f)   // float メンバの範囲比較
                    .ToList();
        Console.WriteLine($"  Height > 1.7: {tall.Count} 件");
        foreach (var p in tall)
            Console.WriteLine($"    {p.Name}, height={p.Height}");

        // ── FT-35 増分2: DateTime の範囲述語 (TimeZone 正準化) ──
        var afterMarch = g.Nodes<Person>()
                          .Where(p => p.CreatedAt > new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc))
                          .ToList();
        Console.WriteLine($"  CreatedAt > 2024-03: {afterMarch.Count} 件");
        foreach (var p in afterMarch)
            Console.WriteLine($"    {p.Name}, created={p.CreatedAt:yyyy-MM-dd}");

        // ── 5. GC-8: エッジ述語付き型保存ホップ (.Knows(e => ...)) ──
        var recentlyKnown = g.Nodes<Person>()
                             .Where(p => p.Name == "Alice")
                             .Knows(e => e.Since == "2024-01")  // ← エッジ Knows.Since で絞り込み (型保存)
                             .ToList();
        Console.WriteLine($"  Alice が 2024-01 に知った: {recentlyKnown.Count} 件");
        foreach (var p in recentlyKnown)
            Console.WriteLine($"    {p.Name}, age={p.Age}");
    }
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

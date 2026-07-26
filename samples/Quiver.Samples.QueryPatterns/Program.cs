// Quiver.Samples.QueryPatterns — 読み取り/書き込みクエリパターン集。
//
// 実行: dotnet run --project samples/Quiver.Samples.QueryPatterns
//
// 1. 型安全 Where / StartsWith
// 2. Coalesce (最初に一致した分岐)
// 3. Optional (任意一致、OPTIONAL MATCH)
// 4. Union (複数分岐の連結)
// 5. As / Select (タプルへの射影)
// 6. 存在条件つき書き込み (MergeVertex / MergeEdge + C# if)
// 7. 直積 AddEdge (発端シナリオ: B-Person → C-Tool に Use を張る)

using Quiver;
using Quiver.Api;
using Quiver.Samples.QueryPatterns;

string dir = Path.Combine(Path.GetTempPath(), "quiver_qp_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));

    // ── データ投入 ──────────────────────────────────────────────────────────
    using (var tx = db.BeginWriteTransaction())
    {
        var g = tx.Query;
        var alice = tx.Mutate.Insert(new Person { Name = "Alice", Age = 30 });
        var bob   = tx.Mutate.Insert(new Person { Name = "Bob",   Age = 25 });
        var carol = tx.Mutate.Insert(new Person { Name = "Carol", Age = 35 });
        var dave  = tx.Mutate.Insert(new Person { Name = "Dave",  Age = 28 });

        var cutter   = tx.Mutate.Insert(new Tool { Name = "Cutter" });
        var compiler = tx.Mutate.Insert(new Tool { Name = "Compiler" });
        var drill    = tx.Mutate.Insert(new Tool { Name = "Drill" });

        Knows.Insert(tx, alice, bob,   new Knows { Since = "2023" });
        Knows.Insert(tx, alice, carol, new Knows { Since = "2024" });
        Knows.Insert(tx, bob,   dave,  new Knows { Since = "2022" });
        Knows.Insert(tx, carol, dave,  new Knows { Since = "2024" });

        Use.Insert(tx, alice, cutter, new Use { Note = "daily" });
        Use.Insert(tx, bob, compiler, new Use { Note = "work" });
        tx.Commit();
    }

    // ── 1. 型安全 Where / StartsWith ────────────────────────────────────────
    Console.WriteLine("── 1. 型安全 Where / StartsWith ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var result = g.Vertices<Person>()
            .Where(p => p.Age > 26 && p.Name.StartsWith("C"))
            .ToList();
        foreach (var p in result)
            Console.WriteLine($"  {p.Name}, age={p.Age}");
        // → Carol, age=35
    }

    // ── 2. 最初に一致した分岐を返す Coalesce ───────────────────────────────
    Console.WriteLine("\n── 2. Coalesce (KNOWS があればその先、なければ自身) ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        // Dave は KNOWS 先が無い → 自身 (Dave) が返る。Alice は Bob, Carol。
        var names = g.Vertices().HasLabel("Person")
            .Coalesce(s => s.Out("KNOWS"), s => s)
            .Dedup()
            .Values("Name")
            .ToList();
        foreach (var n in names)
            Console.WriteLine($"  {n}");
    }

    // ── 3. 一致しなくても元の行を残す Optional ─────────────────────────────
    Console.WriteLine("\n── 3. Optional (KNOWS 先があればそちら、なければ元のまま) ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var names = g.Vertices().HasLabel("Person")
            .Optional(s => s.Out("KNOWS"))
            .Dedup()
            .Values("Name")
            .ToList();
        foreach (var n in names)
            Console.WriteLine($"  {n}");
    }

    // ── 4. 複数分岐を連結する Union ─────────────────────────────────────────
    Console.WriteLine("\n── 4. Union (KNOWS 先 + USE 先を連結) ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var names = g.Vertices().HasLabel("Person").Has("Name", "Alice")
            .Union(s => s.Out("KNOWS"), s => s.Out("USE"))
            .Values("Name")
            .ToList();
        foreach (var n in names)
            Console.WriteLine($"  {n}");
        // → Bob, Carol, Cutter
    }

    // ── 5. ラベル付けと射影を行う As / Select ──────────────────────────────
    Console.WriteLine("\n── 5. As / Select (Person→KNOWS→Person のペア) ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var pairs = g.Vertices().HasLabel("Person").As("src")
            .Out("KNOWS").As("dst")
            .Select(t =>
            {
                string srcName = System.Text.Encoding.UTF8.GetString(tx.GetProperty(t.Vertex("src"), "Name").Utf8StringValue);
                string dstName = System.Text.Encoding.UTF8.GetString(tx.GetProperty(t.Vertex("dst"), "Name").Utf8StringValue);
                return (srcName, dstName);
            });
        foreach (var (src, dst) in pairs)
            Console.WriteLine($"  {src} → {dst}");
    }

    // ── 6. 存在条件つき書き込み (MergeVertex / MergeEdge + C# if) ─────
    Console.WriteLine("\n── 6. MergeVertex / MergeEdge + C# if ──");
    using (var tx = db.BeginWriteTransaction())
    {
        var g = tx.Query;

        // Coalesce ブランチ内での変異は非対応。代替として C# if で分岐する。
        var (eveId, eveCreated) = tx.Mutate.MergeVertex("Person", "Name", Quiver.Storage.Records.PropertyValue.FromString("Eve"));
        if (eveCreated)
            tx.SetProperty(eveId, "Age", Quiver.Storage.Records.PropertyValue.FromInt32(22));
        Console.WriteLine($"  Eve: created={eveCreated}");

        var (_, relCreated) = tx.Mutate.MergeEdge(eveId, g.Vertices().HasLabel("Tool").Has("Name", "Compiler").ToList().First(), "USE");
        Console.WriteLine($"  Eve→Compiler: created={relCreated}");

        // 2 回目は既存ヒット
        var (_, relCreated2) = tx.Mutate.MergeEdge(eveId, g.Vertices().HasLabel("Tool").Has("Name", "Compiler").ToList().First(), "USE");
        Console.WriteLine($"  Eve→Compiler (2 回目): created={relCreated2}");

        tx.Commit();
    }

    // ── 7. 直積 AddEdge (発端シナリオ) ──────────────────────────────────────
    Console.WriteLine("\n── 7. B-Person → C-Tool に Use を直積 AddEdge ──");
    using (var tx = db.BeginWriteTransaction())
    {
        var g = tx.Query;
        long n = tx.Mutate.AddEdge(
            g.Vertices<Person>().Where(p => p.Name.StartsWith("B")),
            g.Vertices<Tool>().Where(t => t.Name.StartsWith("C")),
            (p, t) => new Use { Note = $"{p.Name}→{t.Name}" });
        Console.WriteLine($"  生成辺数: {n}");
        // → Bob × (Cutter, Compiler) = 2

        // MergeEdge で冪等性を確認
        var (created, matched) = tx.Mutate.MergeEdge(
            g.Vertices<Person>().Where(p => p.Name.StartsWith("B")),
            g.Vertices<Tool>().Where(t => t.Name.StartsWith("C")),
            (p, t) => new Use { Note = "overwrite" });
        Console.WriteLine($"  MergeEdge: created={created}, matched={matched}");
        // → created=0, matched=2

        tx.Commit();
    }

    Console.WriteLine("\n完了。");
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

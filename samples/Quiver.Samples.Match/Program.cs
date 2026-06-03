// Quiver.Samples.Match — Match DSL + MERGE。
//
// 実行: dotnet run --project samples/Quiver.Samples.Match

using Quiver;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Storage.Records;

string dir = Path.Combine(Path.GetTempPath(), "quiver_match_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));

    // ── データ投入 ──
    using (var tx = db.BeginTransaction())
    {
        var g = tx.G(db.Schema);
        var alice = g.AddNode("Person").P("name", "Alice").P("age", 30).Next();
        var bob   = g.AddNode("Person").P("name", "Bob").P("age", 25).Next();
        var carol = g.AddNode("Person").P("name", "Carol").P("age", 35).Next();
        g.AddRelationship("KNOWS").From(alice).To(bob).Next();
        g.AddRelationship("KNOWS").From(alice).To(carol).Next();
        g.AddRelationship("KNOWS").From(bob).To(carol).Next();
        tx.Commit();
    }

    // ── Match DSL ──
    Console.WriteLine("── 1. Match DSL: (n:Person)-[:KNOWS]->(m:Person) WHERE n.age > 25 ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
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

        foreach (var p in pairs)
            Console.WriteLine($"  {p.From} → {p.To}");
    }

    // ── MERGE: 重複なし upsert ──
    Console.WriteLine();
    Console.WriteLine("── 2. MERGE で同じメアドのノードを重複作成しない ──");
    using (var tx = db.BeginTransaction())
    {
        var (id1, created1) = tx.MergeNode(
            "Person", "email", PropertyValue.FromString("dave@example.com"));
        Console.WriteLine($"  1 回目 MergeNode: created={created1}, id={id1.Value}");

        var (id2, created2) = tx.MergeNode(
            "Person", "email", PropertyValue.FromString("dave@example.com"));
        Console.WriteLine($"  2 回目 MergeNode: created={created2}, id={id2.Value}");

        tx.Commit();
    }
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

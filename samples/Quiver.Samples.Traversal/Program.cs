// Quiver.Samples.Traversal — 多段トラバーサル: フィルタ・並び替え・集約・可変長 repeat。
//
// 実行: dotnet run --project samples/Quiver.Samples.Traversal

using Quiver;
using Quiver.Client;
using Quiver.Core;

string dir = Path.Combine(Path.GetTempPath(), "quiver_traversal_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = GraphDatabase.Open(dir);
    using var tx = db.BeginTransaction();
    var g = tx.G(db.Schema);

    // ソーシャルグラフ: Alice→Bob→Dave, Alice→Carol→Dave, Carol→Eve
    var alice = g.AddNode("Person").P("name", "Alice").P("age", 30).Next();
    var bob   = g.AddNode("Person").P("name", "Bob").P("age", 25).Next();
    var carol = g.AddNode("Person").P("name", "Carol").P("age", 35).Next();
    var dave  = g.AddNode("Person").P("name", "Dave").P("age", 28).Next();
    var eve   = g.AddNode("Person").P("name", "Eve").P("age", 40).Next();

    g.AddRelationship("KNOWS").From(alice).To(bob).Next();
    g.AddRelationship("KNOWS").From(alice).To(carol).Next();
    g.AddRelationship("KNOWS").From(bob).To(dave).Next();
    g.AddRelationship("KNOWS").From(carol).To(dave).Next();
    g.AddRelationship("FOLLOWS").From(carol).To(eve).Next();

    Console.WriteLine("── 1. フィルタ + ページング ──");
    var seniors = g.Nodes().HasLabel("Person")
                   .Has("age", P.Gt(28L))
                   .OrderByDescending("age")
                   .Values("name")
                   .ToList();
    Console.WriteLine($"  age > 28 の人 (年齢降順): {string.Join(", ", seniors)}");

    Console.WriteLine();
    Console.WriteLine("── 2. 隣接ノード ──");
    var aliceFriends = g.Node(alice).Out("KNOWS").Values("name").ToList();
    Console.WriteLine($"  Alice の KNOWS 先: {string.Join(", ", aliceFriends)}");

    Console.WriteLine();
    Console.WriteLine("── 3. 2 ホップ + 重複排除 ──");
    var twoHop = g.Node(alice)
                  .Repeat(s => s.Out("KNOWS"), times: 2)
                  .Dedup()
                  .Values("name")
                  .ToList();
    Console.WriteLine($"  Alice から 2 ホップ: {string.Join(", ", twoHop)}");

    Console.WriteLine();
    Console.WriteLine("── 4. 集約 ──");
    double avgAge = g.Nodes().HasLabel("Person").Mean("age") ?? 0.0;
    Console.WriteLine($"  Person.age 平均: {avgAge:F1}");

    Console.WriteLine();
    Console.WriteLine("── 5. WHERE EXISTS サブトラバーサル ──");
    var connectors = g.Nodes().HasLabel("Person")
                      .Where(t => t.Out("KNOWS").HasLabel("Person"))
                      .Values("name")
                      .ToList();
    Console.WriteLine($"  KNOWS 先に Person を持つ人: {string.Join(", ", connectors)}");

    Console.WriteLine();
    Console.WriteLine("── 6. ストリーミング cursor ──");
    using (var cursor = g.Nodes().HasLabel("Person").Values("name").AsCursor())
    {
        int n = 0;
        while (cursor.MoveNext() && n++ < 3)
            Console.WriteLine($"    {cursor.Current}");
    }

    tx.Rollback();
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

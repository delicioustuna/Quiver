// Quiver.Samples.Crud — 基本 CRUD: Vertex作成、プロパティ設定、リレーション作成、削除。
//
// 実行: dotnet run --project samples/Quiver.Samples.Crud

using Quiver;
using Quiver.Storage.Records;

string dir = Path.Combine(Path.GetTempPath(), "quiver_crud_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
    using var tx = db.BeginWriteTransaction();

    Console.WriteLine("── 1. Vertex作成 + プロパティ ──");
    var alice = tx.CreateVertex("Person");
    var bob   = tx.CreateVertex("Person");
    var carol = tx.CreateVertex("Person");

    tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
    tx.SetProperty(bob,   "name", PropertyValue.FromString("Bob"));
    tx.SetProperty(carol, "name", PropertyValue.FromString("Carol"));
    tx.SetProperty(alice, "age",  PropertyValue.FromInt32(30));
    tx.SetProperty(bob,   "age",  PropertyValue.FromInt32(25));
    tx.SetProperty(carol, "age",  PropertyValue.FromInt32(35));

    Console.WriteLine($"  alice 存在チェック: {tx.VertexExists(alice)}");
    var aliceName = Str(tx.GetProperty(alice, "name"));
    Console.WriteLine($"  alice.name = {aliceName}");
    Console.WriteLine($"  alice.age  = {tx.GetProperty(alice, "age").Int32Value}");

    Console.WriteLine();
    Console.WriteLine("── 2. リレーション作成 ──");
    tx.CreateEdge(alice, bob,   "KNOWS");
    tx.CreateEdge(alice, carol, "KNOWS");
    tx.CreateEdge(bob,   carol, "FOLLOWS");

    Console.WriteLine("  alice の隣接Vertex一覧:");
    var en = tx.EnumerateEdges(alice);
    while (en.MoveNext())
    {
        var r = en.Current;
        var neighbor = r.Source == alice ? r.Target : r.Source;
        var neighborName = Str(tx.GetProperty(neighbor, "name"));
        Console.WriteLine($"    → {neighborName}");
    }

    Console.WriteLine();
    Console.WriteLine("── 3. 更新と削除 ──");
    tx.SetProperty(alice, "age", PropertyValue.FromInt32(31));
    Console.WriteLine($"  alice.age (更新後) = {tx.GetProperty(alice, "age").Int32Value}");

    tx.RemoveProperty(alice, "age");
    Console.WriteLine($"  age 削除後 HasProperty(age) = {tx.HasProperty(alice, "age")}");

    var relEn = tx.EnumerateEdges(alice, Direction.Both, "KNOWS");
    if (relEn.MoveNext())
        tx.DeleteEdge(relEn.Current.Id);
    Console.WriteLine("  KNOWS リレーションを 1 件削除しました。");

    tx.Commit();
    Console.WriteLine();
    Console.WriteLine("CRUD サンプル完了。");
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

static string Str(PropertyValue v) =>
    System.Text.Encoding.UTF8.GetString(v.Utf8StringValue);

// Quiver.Samples.AsyncApi — 非同期トランザクション境界と traversal 終端の利用例。
//
// 実行: dotnet run --project samples/Quiver.Samples.AsyncApi

using Quiver;
using Quiver.Api;
using Quiver.Storage.Records;

string dir = Path.Combine(Path.GetTempPath(), "quiver_async_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    // GraphDatabase と IGraphTransaction は IAsyncDisposable に対応しているため、
    // ASP.NET Core や UI アプリの非同期ライフサイクルへそのまま組み込める。
    await using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));

    await using (var tx = await db.BeginTransactionAsync())
    {
        // グラフ操作は mmap 上の同期処理。ThreadStatic のトランザクションコンテキストを
        // 維持するため、BeginTransactionAsync から CommitAsync まで外部 await を挟まない。
        foreach (var name in new[] { "Alice", "Bob", "Carol" })
        {
            var node = tx.CreateNode("Person");
            tx.SetProperty(node, "name", PropertyValue.FromString(name));
        }

        // WAL の fsync 待機だけを非同期化し、呼び出し元スレッドをブロックしない。
        await tx.CommitAsync();
    }

    await using var read = await db.BeginReadOnlyTransactionAsync();
    var people = read.G(db.Schema).Nodes().HasLabel("Person");

    // ToListAsync / CountAsync は同期 traversal と同じ結果を ValueTask で返す。
    var ids = await people.ToListAsync();
    var count = await people.CountAsync();
    Console.WriteLine($"Person 件数: {count}");

    // AsAsyncEnumerable は全件を List に具体化せず、カーソルを逐次消費する。
    await foreach (var id in people.AsAsyncEnumerable())
    {
        var name = System.Text.Encoding.UTF8.GetString(
            read.GetProperty(id, "name").Utf8StringValue);
        Console.WriteLine($"  {id.Value}: {name}");
    }

    Console.WriteLine($"ToListAsync で取得した ID 件数: {ids.Count}");
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

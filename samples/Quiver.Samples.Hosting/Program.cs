// Quiver.Samples.Hosting — ASP.NET Core minimal API から Quiver を DI 経由で利用するサンプル。
//
// 実行: dotnet run --project samples/Quiver.Samples.Hosting
//
// 設定オーバーライド例 (環境変数):
//   set Quiver__DataDirectory=C:\data\quiver
//   set Quiver__BufferPoolSize=536870912

using Quiver;
using Quiver.Core;
using Quiver.Hosting;
using Quiver.Stores;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddQuiver(builder.Configuration.GetSection("Quiver"));

var app = builder.Build();

app.MapGet("/", () => "Quiver hosting sample. POST /nodes / GET /nodes/{id}");

app.MapPost("/nodes", (CreateNodeRequest req, GraphDatabase db) =>
{
    using var tx = db.BeginTransaction();
    var id = tx.CreateNode(req.Label);
    if (!string.IsNullOrEmpty(req.Name))
        tx.SetProperty(id, "name", PropertyValue.FromString(req.Name));
    tx.Commit();
    return Results.Created($"/nodes/{id.Value}", new { id = id.Value, label = req.Label, name = req.Name });
});

app.MapGet("/nodes/{id:long}", (long id, GraphDatabase db) =>
{
    using var tx = db.BeginReadOnlyTransaction();
    var nid = new NodeId(id);
    // NodeExists / HasProperty / GetProperty は HWM を超えた ID で
    // Quiver.Core.CorruptionException を投げる (page magic ゼロ判定)。
    // sample API としては 404 に丸める。
    try
    {
        if (!tx.NodeExists(nid))
            return Results.NotFound();
        var name = tx.HasProperty(nid, "name")
            ? System.Text.Encoding.UTF8.GetString(tx.GetProperty(nid, "name").Utf8StringValue)
            : null;
        return Results.Ok(new { id, name });
    }
    catch (CorruptionException)
    {
        return Results.NotFound();
    }
});

app.Run();

internal sealed record CreateNodeRequest(string Label, string? Name);

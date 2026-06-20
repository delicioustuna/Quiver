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
using Quiver.Storage.Records;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddQuiver(builder.Configuration.GetSection("Quiver"));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "Quiver Hosting Sample",
    endpoints = new[]
    {
        "POST /nodes { label, name? }",
        "GET  /nodes/{id}",
        "DELETE /nodes/{id}",
        "POST /nodes/{id}/properties { key, value }",
        "POST /relationships { source, target, type }",
        "GET  /relationships/{id}",
        "GET  /stats",
    },
}));

app.MapPost("/nodes", (CreateNodeRequest? req, GraphDatabase db) =>
{
    if (req is null || string.IsNullOrEmpty(req.Label))
        return Results.BadRequest(new { error = "label is required" });
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
    // HWM 超 / 負 ID は安全にreturn される。
    if (!tx.NodeExists(nid))
        return Results.NotFound();
    var name = tx.HasProperty(nid, "name")
        ? System.Text.Encoding.UTF8.GetString(tx.GetProperty(nid, "name").Utf8StringValue)
        : null;
    return Results.Ok(new { id, name });
});

app.MapDelete("/nodes/{id:long}", (long id, GraphDatabase db) =>
{
    using var tx = db.BeginTransaction();
    var nid = new NodeId(id);
    if (!tx.NodeExists(nid))
        return Results.NotFound();
    tx.DeleteNode(nid);
    tx.Commit();
    return Results.NoContent();
});

app.MapPost("/nodes/{id:long}/properties", (long id, SetPropertyRequest? req, GraphDatabase db) =>
{
    if (req is null || string.IsNullOrEmpty(req.Key))
        return Results.BadRequest(new { error = "key is required" });
    using var tx = db.BeginTransaction();
    var nid = new NodeId(id);
    if (!tx.NodeExists(nid))
        return Results.NotFound();
    tx.SetProperty(nid, req.Key, PropertyValue.FromString(req.Value ?? string.Empty));
    tx.Commit();
    return Results.NoContent();
});

app.MapPost("/relationships", (CreateRelationshipRequest? req, GraphDatabase db) =>
{
    if (req is null || string.IsNullOrEmpty(req.Type))
        return Results.BadRequest(new { error = "type is required" });
    using var tx = db.BeginTransaction();
    var src = new NodeId(req.Source);
    var tgt = new NodeId(req.Target);
    if (!tx.NodeExists(src) || !tx.NodeExists(tgt))
        return Results.NotFound(new { error = "source or target node does not exist" });
    var rid = tx.CreateRelationship(src, tgt, req.Type);
    tx.Commit();
    return Results.Created($"/relationships/{rid.Value}",
        new { id = rid.Value, source = req.Source, target = req.Target, type = req.Type });
});

app.MapGet("/relationships/{id:long}", (long id, GraphDatabase db) =>
{
    using var tx = db.BeginReadOnlyTransaction();
    // GraphTransaction には RelationshipExists が無いので Stats / NodeExists 系のみ。
    // ここではノードと同じ HWM 安全契約を期待するが、現状の IGraphTransaction には
    // RelationshipExists API が無いので存在チェックは sample 範囲では省略する。
    // (将来 API 追加時にここを補強する)
    _ = tx;
    return Results.Ok(new { id });
});

app.MapGet("/stats", (GraphDatabase db) =>
{
    var stats = db.Diagnostics.GetStatistics();
    return Results.Ok(new { nodeCount = stats.NodeCount, relationshipCount = stats.RelationshipCount });
});

app.Run();

internal sealed record CreateNodeRequest(string Label, string? Name);
internal sealed record SetPropertyRequest(string Key, string? Value);
internal sealed record CreateRelationshipRequest(long Source, long Target, string Type);

// Quiver.Hosting.Tests から WebApplicationFactory<Program> で起動するために
// 暗黙の Program クラスを public partial として公開する。
public partial class Program { }

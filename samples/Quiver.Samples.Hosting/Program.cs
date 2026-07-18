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
        "POST /vertices { label, name? }",
        "GET  /vertices/{id}",
        "DELETE /vertices/{id}",
        "POST /vertices/{id}/properties { key, value }",
        "POST /edges { source, target, type }",
        "GET  /edges/{id}",
        "GET  /stats",
    },
}));

app.MapPost("/vertices", (CreateVertexRequest? req, QuiverDatabase db) =>
{
    if (req is null || string.IsNullOrEmpty(req.Label))
        return Results.BadRequest(new { error = "label is required" });
    using var tx = db.BeginWriteTransaction();
    var id = tx.CreateVertex(req.Label);
    if (!string.IsNullOrEmpty(req.Name))
        tx.SetProperty(id, "name", PropertyValue.FromString(req.Name));
    tx.Commit();
    return Results.Created($"/vertices/{id.Value}", new { id = id.Value, label = req.Label, name = req.Name });
});

app.MapGet("/vertices/{id:long}", (long id, QuiverDatabase db) =>
{
    using var tx = db.BeginReadTransaction();
    var nid = new VertexId(id);
    // HWM 超 / 負 ID は安全にreturn される。
    if (!tx.VertexExists(nid))
        return Results.NotFound();
    var name = tx.HasProperty(nid, "name")
        ? System.Text.Encoding.UTF8.GetString(tx.GetProperty(nid, "name").Utf8StringValue)
        : null;
    return Results.Ok(new { id, name });
});

app.MapDelete("/vertices/{id:long}", (long id, QuiverDatabase db) =>
{
    using var tx = db.BeginWriteTransaction();
    var nid = new VertexId(id);
    if (!tx.VertexExists(nid))
        return Results.NotFound();
    tx.DeleteVertex(nid);
    tx.Commit();
    return Results.NoContent();
});

app.MapPost("/vertices/{id:long}/properties", (long id, SetPropertyRequest? req, QuiverDatabase db) =>
{
    if (req is null || string.IsNullOrEmpty(req.Key))
        return Results.BadRequest(new { error = "key is required" });
    using var tx = db.BeginWriteTransaction();
    var nid = new VertexId(id);
    if (!tx.VertexExists(nid))
        return Results.NotFound();
    tx.SetProperty(nid, req.Key, PropertyValue.FromString(req.Value ?? string.Empty));
    tx.Commit();
    return Results.NoContent();
});

app.MapPost("/edges", (CreateEdgeRequest? req, QuiverDatabase db) =>
{
    if (req is null || string.IsNullOrEmpty(req.Type))
        return Results.BadRequest(new { error = "type is required" });
    using var tx = db.BeginWriteTransaction();
    var src = new VertexId(req.Source);
    var tgt = new VertexId(req.Target);
    if (!tx.VertexExists(src) || !tx.VertexExists(tgt))
        return Results.NotFound(new { error = "source or target vertex does not exist" });
    var rid = tx.CreateEdge(src, tgt, req.Type);
    tx.Commit();
    return Results.Created($"/edges/{rid.Value}",
        new { id = rid.Value, source = req.Source, target = req.Target, type = req.Type });
});

app.MapGet("/edges/{id:long}", (long id, QuiverDatabase db) =>
{
    using var tx = db.BeginReadTransaction();
    // GraphTransaction には EdgeExists が無いので Stats / VertexExists 系のみ。
    // ここではVertexと同じ HWM 安全契約を期待するが、現状の IWriteTransaction には
    // EdgeExists API が無いので存在チェックは sample 範囲では省略する。
    // (将来 API 追加時にここを補強する)
    _ = tx;
    return Results.Ok(new { id });
});

app.MapGet("/stats", (QuiverDatabase db) =>
{
    var stats = db.Diagnostics.GetStatistics();
    return Results.Ok(new { vertexCount = stats.VertexCount, edgeCount = stats.EdgeCount });
});

app.Run();

internal sealed record CreateVertexRequest(string Label, string? Name);
internal sealed record SetPropertyRequest(string Key, string? Value);
internal sealed record CreateEdgeRequest(long Source, long Target, string Type);

// Quiver.Hosting.Tests から WebApplicationFactory<Program> で起動するために
// 暗黙の Program クラスを public partial として公開する。
public partial class Program { }

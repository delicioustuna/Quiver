// Quiver.Samples.Hosting — ASP.NET Core minimal API から Quiver を DI 経由で利用するサンプル。
//
// 実行: dotnet run --project samples/Quiver.Samples.Hosting
//
// 設定オーバーライド例 (環境変数):
//   set Quiver__DataDirectory=C:\data\quiver
//   set Quiver__BufferPoolSize=536870912

using Quiver;
using Quiver.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddQuiver(builder.Configuration.GetSection("Quiver"));

var app = builder.Build();
var vertices = new System.Collections.Concurrent.ConcurrentDictionary<Guid, VertexKey>();
var edges = new System.Collections.Concurrent.ConcurrentDictionary<Guid, EdgeKey>();

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

app.MapPost("/vertices", (CreateVertexRequest? req, GraphStore store) =>
{
    if (req is null || string.IsNullOrEmpty(req.Label))
        return Results.BadRequest(new { error = "label is required" });
    VertexKey key = store.Write(write =>
    {
        VertexKey created = write.CreateVertex(req.Label);
        if (!string.IsNullOrEmpty(req.Name))
            write.Set(created, "name", req.Name);
        return created;
    });
    Guid id = Guid.NewGuid();
    vertices[id] = key;
    return Results.Created($"/vertices/{id}", new { id, label = req.Label, name = req.Name });
});

app.MapGet("/vertices/{id:guid}", (Guid id, GraphStore store) =>
{
    if (!vertices.TryGetValue(id, out VertexKey key))
        return Results.NotFound();
    string? name = store.Read(read =>
        read.Contains(key) && read.TryGet(key, "name", out GraphValue value)
            ? value.AsString()
            : null);
    return Results.Ok(new { id, name });
});

app.MapDelete("/vertices/{id:guid}", (Guid id, GraphStore store) =>
{
    if (!vertices.TryRemove(id, out VertexKey key))
        return Results.NotFound();
    store.Write(write => write.Delete(key));
    return Results.NoContent();
});

app.MapPost("/vertices/{id:guid}/properties", (Guid id, SetPropertyRequest? req, GraphStore store) =>
{
    if (req is null || string.IsNullOrEmpty(req.Key))
        return Results.BadRequest(new { error = "key is required" });
    if (!vertices.TryGetValue(id, out VertexKey key))
        return Results.NotFound();
    store.Write(write => write.Set(key, req.Key, req.Value ?? string.Empty));
    return Results.NoContent();
});

app.MapPost("/edges", (CreateEdgeRequest? req, GraphStore store) =>
{
    if (req is null || string.IsNullOrEmpty(req.Type))
        return Results.BadRequest(new { error = "type is required" });
    if (!vertices.TryGetValue(req.Source, out VertexKey source)
        || !vertices.TryGetValue(req.Target, out VertexKey target))
        return Results.NotFound(new { error = "source or target vertex does not exist" });
    EdgeKey key = store.Write(write => write.Connect(source, req.Type, target));
    Guid id = Guid.NewGuid();
    edges[id] = key;
    return Results.Created($"/edges/{id}",
        new { id, source = req.Source, target = req.Target, type = req.Type });
});

app.MapGet("/edges/{id:guid}", (Guid id) =>
{
    return edges.ContainsKey(id) ? Results.Ok(new { id }) : Results.NotFound();
});

app.MapGet("/stats", (GraphStore store) =>
{
    long vertexCount = store.Read(read => read.Query.Vertices().Count());
    return Results.Ok(new { vertexCount, edgeCount = edges.Count });
});

app.Run();

internal sealed record CreateVertexRequest(string Label, string? Name);
internal sealed record SetPropertyRequest(string Key, string? Value);
internal sealed record CreateEdgeRequest(Guid Source, Guid Target, string Type);

// Quiver.Hosting.Tests から WebApplicationFactory<Program> で起動するために
// 暗黙の Program クラスを public partial として公開する。
public partial class Program { }

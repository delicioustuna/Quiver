using Microsoft.Extensions.Logging;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Studio.Services;

public sealed class GraphEditingService
{
    private readonly DatabaseService _db;
    private readonly ILogger<GraphEditingService> _logger;

    public GraphEditingService(DatabaseService databaseService, ILogger<GraphEditingService> logger)
    {
        _db = databaseService;
        _logger = logger;
    }

    public VertexId CreateVertex(string label, IReadOnlyList<(string key, string value)>? properties = null)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginWriteTransaction();
        var nid = tx.CreateVertex(label);

        if (properties is not null)
        {
            foreach (var (key, value) in properties)
                tx.SetProperty(nid, key, PropertyValue.FromString(value));
        }

        tx.Commit();
        _db.RefreshStatistics();
        _logger.LogInformation("Vertex作成: {Id} label={Label}", nid, label);
        return nid;
    }

    public void DeleteVertex(VertexId id)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginWriteTransaction();

        var edges = tx.EnumerateEdges(id);
        var edgeIds = new List<EdgeId>();
        while (edges.MoveNext())
            edgeIds.Add(edges.Current.Id);
        foreach (var rid in edgeIds)
            tx.DeleteEdge(rid);

        tx.DeleteVertex(id);
        tx.Commit();
        _db.RefreshStatistics();
        _logger.LogInformation("Vertex削除: {Id}", id);
    }

    public EdgeId CreateEdge(VertexId source, VertexId target, string type)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginWriteTransaction();
        var rid = tx.CreateEdge(source, target, type);
        tx.Commit();
        _db.RefreshStatistics();
        _logger.LogInformation("Edge作成: {Id} ({Source})-[{Type}]->({Target})", rid, source, type, target);
        return rid;
    }

    public void DeleteEdge(EdgeId id)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginWriteTransaction();
        tx.DeleteEdge(id);
        tx.Commit();
        _db.RefreshStatistics();
        _logger.LogInformation("Edge削除: {Id}", id);
    }

    public void SetProperty(VertexId id, string key, string value)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginWriteTransaction();
        tx.SetProperty(id, key, PropertyValue.FromString(value));
        tx.Commit();
        _logger.LogInformation("プロパティ設定: Vertex {Id} {Key}={Value}", id, key, value);
    }

    public void RemoveProperty(VertexId id, string key)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginWriteTransaction();
        tx.RemoveProperty(id, key);
        tx.Commit();
        _logger.LogInformation("プロパティ削除: Vertex {Id} {Key}", id, key);
    }

    public void SetEdgeProperty(EdgeId id, string key, string value)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginWriteTransaction();
        tx.SetProperty(id, key, PropertyValue.FromString(value));
        tx.Commit();
        _logger.LogInformation("プロパティ設定: Edge {Id} {Key}={Value}", id, key, value);
    }
}

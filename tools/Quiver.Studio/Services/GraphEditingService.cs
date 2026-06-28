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

    public NodeId CreateNode(string label, IReadOnlyList<(string key, string value)>? properties = null)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginTransaction();
        var nid = tx.CreateNode(label);

        if (properties is not null)
        {
            foreach (var (key, value) in properties)
                tx.SetProperty(nid, key, PropertyValue.FromString(value));
        }

        tx.Commit();
        _db.RefreshStatistics();
        _logger.LogInformation("ノード作成: {Id} label={Label}", nid, label);
        return nid;
    }

    public void DeleteNode(NodeId id)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginTransaction();

        var rels = tx.EnumerateRelationships(id);
        var relIds = new List<RelationshipId>();
        while (rels.MoveNext())
            relIds.Add(rels.Current.Id);
        foreach (var rid in relIds)
            tx.DeleteRelationship(rid);

        tx.DeleteNode(id);
        tx.Commit();
        _db.RefreshStatistics();
        _logger.LogInformation("ノード削除: {Id}", id);
    }

    public RelationshipId CreateRelationship(NodeId source, NodeId target, string type)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginTransaction();
        var rid = tx.CreateRelationship(source, target, type);
        tx.Commit();
        _db.RefreshStatistics();
        _logger.LogInformation("リレーションシップ作成: {Id} ({Source})-[{Type}]->({Target})", rid, source, type, target);
        return rid;
    }

    public void DeleteRelationship(RelationshipId id)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginTransaction();
        tx.DeleteRelationship(id);
        tx.Commit();
        _db.RefreshStatistics();
        _logger.LogInformation("リレーションシップ削除: {Id}", id);
    }

    public void SetProperty(NodeId id, string key, string value)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginTransaction();
        tx.SetProperty(id, key, PropertyValue.FromString(value));
        tx.Commit();
        _logger.LogInformation("プロパティ設定: Node {Id} {Key}={Value}", id, key, value);
    }

    public void RemoveProperty(NodeId id, string key)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginTransaction();
        tx.RemoveProperty(id, key);
        tx.Commit();
        _logger.LogInformation("プロパティ削除: Node {Id} {Key}", id, key);
    }

    public void SetRelationshipProperty(RelationshipId id, string key, string value)
    {
        var db = _db.CurrentDatabase ?? throw new InvalidOperationException("No database open.");
        using var tx = db.BeginTransaction();
        tx.SetProperty(id, key, PropertyValue.FromString(value));
        tx.Commit();
        _logger.LogInformation("プロパティ設定: Relationship {Id} {Key}={Value}", id, key, value);
    }
}

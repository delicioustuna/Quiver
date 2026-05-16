using Quiver.Core;

namespace Quiver.Logical;

/// <summary>
/// BA-7 / codex_advice_3 §8. A semantic record of one graph mutation.
///
/// Logical mutations are produced by a writing <see cref="IGraphTransaction"/>
/// after each public mutation call, buffered until commit, and handed to a
/// <see cref="ILogicalMutationSink"/> once the underlying transaction has
/// durably committed (binary backend: WAL flush; SQLite backend: COMMIT).
///
/// The record is self-contained — label / relationship-type / property-key
/// names are captured as strings rather than tokens so the stream can be
/// inspected, shipped, and replayed against a graph that has not yet seen
/// those tokens (token ids will differ between source and target databases).
/// </summary>
public readonly struct LogicalMutation
{
    public LogicalMutationKind Kind { get; }

    /// <summary>Primary node id (CreateNode / DeleteNode / *NodeProperty / CreateRelationship source).</summary>
    public NodeId NodeId { get; }

    /// <summary>CreateRelationship target node.</summary>
    public NodeId TargetNodeId { get; }

    /// <summary>Relationship id (CreateRelationship return / DeleteRelationship / SetRelationshipProperty).</summary>
    public RelationshipId RelationshipId { get; }

    /// <summary>Label name for <see cref="LogicalMutationKind.CreateNode"/>; relationship type for <see cref="LogicalMutationKind.CreateRelationship"/>.</summary>
    public string? TokenName { get; }

    /// <summary>Property key name for *Property mutations.</summary>
    public string? PropertyKey { get; }

    /// <summary>Property value for <see cref="LogicalMutationKind.SetNodeProperty"/> / <see cref="LogicalMutationKind.SetRelationshipProperty"/>.</summary>
    public LogicalPropertyValue PropertyValue { get; }

    private LogicalMutation(
        LogicalMutationKind kind,
        NodeId nodeId = default,
        NodeId targetNodeId = default,
        RelationshipId relationshipId = default,
        string? tokenName = null,
        string? propertyKey = null,
        LogicalPropertyValue propertyValue = default)
    {
        Kind = kind;
        NodeId = nodeId;
        TargetNodeId = targetNodeId;
        RelationshipId = relationshipId;
        TokenName = tokenName;
        PropertyKey = propertyKey;
        PropertyValue = propertyValue;
    }

    public static LogicalMutation CreateNode(NodeId nodeId, string label)
        => new(LogicalMutationKind.CreateNode, nodeId: nodeId, tokenName: label);

    public static LogicalMutation DeleteNode(NodeId nodeId)
        => new(LogicalMutationKind.DeleteNode, nodeId: nodeId);

    public static LogicalMutation CreateRelationship(
        RelationshipId relId, NodeId source, NodeId target, string type)
        => new(LogicalMutationKind.CreateRelationship,
            nodeId: source, targetNodeId: target,
            relationshipId: relId, tokenName: type);

    public static LogicalMutation DeleteRelationship(RelationshipId relId)
        => new(LogicalMutationKind.DeleteRelationship, relationshipId: relId);

    public static LogicalMutation SetNodeProperty(NodeId nodeId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.SetNodeProperty, nodeId: nodeId, propertyKey: key, propertyValue: value);

    public static LogicalMutation SetRelationshipProperty(RelationshipId relId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.SetRelationshipProperty,
            relationshipId: relId, propertyKey: key, propertyValue: value);

    public static LogicalMutation RemoveNodeProperty(NodeId nodeId, string key)
        => new(LogicalMutationKind.RemoveNodeProperty, nodeId: nodeId, propertyKey: key);
}

using Quiver.Core;

namespace Quiver.Logical;

/// <summary>
/// BA-7 helper that re-applies a captured <see cref="LogicalMutation"/> stream
/// against a fresh <see cref="IGraphTransaction"/>. Node and relationship ids
/// are remapped on the fly because the target database assigns its own ids —
/// callers can supply pre-seeded maps to chain multiple replay passes.
/// </summary>
public static class LogicalMutationReplay
{
    /// <summary>
    /// Apply <paramref name="mutations"/> in order. The transaction is the
    /// caller's responsibility — it must be writable, and the caller commits
    /// (or rolls back) when replay finishes so multiple batches can be folded
    /// into one outer transaction.
    /// </summary>
    /// <param name="tx">Target transaction.</param>
    /// <param name="mutations">Mutation stream, typically from a
    /// <see cref="ILogicalMutationSink"/>.</param>
    /// <param name="nodeMap">Optional source-id → target-id node map. Mutated.</param>
    /// <param name="relationshipMap">Optional source-id → target-id relationship map. Mutated.</param>
    public static void Apply(
        IGraphTransaction tx,
        IEnumerable<LogicalMutation> mutations,
        IDictionary<long, NodeId>? nodeMap = null,
        IDictionary<long, RelationshipId>? relationshipMap = null)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(mutations);
        nodeMap ??= new Dictionary<long, NodeId>();
        relationshipMap ??= new Dictionary<long, RelationshipId>();

        foreach (var m in mutations)
        {
            switch (m.Kind)
            {
                case LogicalMutationKind.CreateNode:
                {
                    var newId = tx.CreateNode(m.TokenName ?? string.Empty);
                    nodeMap[m.NodeId.Sequence] = newId;
                    break;
                }
                case LogicalMutationKind.DeleteNode:
                {
                    if (nodeMap.TryGetValue(m.NodeId.Sequence, out var nodeId))
                        tx.DeleteNode(nodeId);
                    break;
                }
                case LogicalMutationKind.CreateRelationship:
                {
                    if (!nodeMap.TryGetValue(m.NodeId.Sequence, out var src)) break;
                    if (!nodeMap.TryGetValue(m.TargetNodeId.Sequence, out var tgt)) break;
                    var newId = tx.CreateRelationship(src, tgt, m.TokenName ?? string.Empty);
                    relationshipMap[m.RelationshipId.Sequence] = newId;
                    break;
                }
                case LogicalMutationKind.DeleteRelationship:
                {
                    if (relationshipMap.TryGetValue(m.RelationshipId.Sequence, out var relId))
                        tx.DeleteRelationship(relId);
                    break;
                }
                case LogicalMutationKind.SetNodeProperty:
                {
                    if (!nodeMap.TryGetValue(m.NodeId.Sequence, out var nodeId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.SetProperty(nodeId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.SetRelationshipProperty:
                {
                    if (!relationshipMap.TryGetValue(m.RelationshipId.Sequence, out var relId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.SetProperty(relId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.RemoveNodeProperty:
                {
                    if (nodeMap.TryGetValue(m.NodeId.Sequence, out var nodeId))
                        tx.RemoveProperty(nodeId, m.PropertyKey ?? string.Empty);
                    break;
                }
            }
        }
    }
}

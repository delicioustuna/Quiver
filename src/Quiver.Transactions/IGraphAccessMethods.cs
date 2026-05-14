using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Transactions;

/// <summary>
/// Backend access-method contract (BA-3, codex_advice_3.md §1). Operators route
/// scan / seek / expand through this interface instead of directly poking
/// <see cref="ITransaction.Nodes"/> / <see cref="ITransaction.Relationships"/> /
/// <see cref="ITransaction.AdjacencyBlocks"/>, so each backend can choose its
/// own access path (linked-list, adjacency-block, relationship scan, etc.).
/// </summary>
public interface IGraphAccessMethods
{
    /// <summary>Enumerate live nodes, optionally constrained to a single label.</summary>
    IEnumerable<NodeId> ScanNodes(ITransaction tx, LabelId? label = null);

    /// <summary>
    /// Seek a B+Tree index by exact key match. Routes to the correct typed
    /// index based on <paramref name="key"/>'s <see cref="PropertyValue.Type"/>.
    /// Returns an empty sequence when the index is missing or the type is unsupported.
    /// </summary>
    IEnumerable<NodeId> SeekNodesByIndex(ITransaction tx, string indexName, PropertyValue key);

    /// <summary>
    /// Open a cursor over edges incident to <paramref name="source"/> matching
    /// the requested direction and optional type filter. The cursor owns the
    /// choice of access path (adjacency block with linked-list fallback for
    /// the binary backend) and bumps <see cref="AdjacencyFallbackCount"/>
    /// whenever it drops back to the slower path.
    /// </summary>
    ExpandCursor Expand(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter);

    /// <summary>
    /// Estimated number of edges that <see cref="Expand"/> would emit. Used by
    /// optimizer plan selection to size buffers / pick strategies.
    /// </summary>
    double EstimateExpandCardinality(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter);

    /// <summary>
    /// Lifetime counter for how often the expand cursor abandoned a fast path
    /// (e.g. adjacency block buffer filled exactly) and fell back to the
    /// linked-list walk. Surfaced via <c>IDiagnosticsApi.GetStatistics</c>.
    /// </summary>
    long AdjacencyFallbackCount { get; }
}

/// <summary>
/// Backend-owned cursor for one-hop expansion. Replaces the inline
/// adjacency-block-or-linked-list bookkeeping that previously lived in
/// <c>ExpandOperator</c> / <c>BfsOperator</c>.
/// </summary>
public abstract class ExpandCursor : IDisposable
{
    public abstract bool MoveNext();
    public abstract NodeId Neighbor { get; }
    public abstract RelationshipId Relationship { get; }
    public virtual void Dispose() { }
}

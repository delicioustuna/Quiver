using Quiver.Core;

namespace Quiver.Stores;

/// <summary>
/// Read interface for the contiguous adjacency block store.
/// Filled by BulkLoader.Commit(buildAdjacencyIndex: true); immutable afterward.
/// </summary>
public interface IAdjacencyBlockStore
{
    /// <summary>Returns true when the node has an adjacency block (i.e., was present at index build time).</summary>
    bool HasBlock(NodeId nodeId);

    /// <summary>
    /// Fills <paramref name="buffer"/> with edges matching <paramref name="direction"/> and optional
    /// <paramref name="typeFilter"/>. Returns the count written. If the return value equals
    /// <c>buffer.Length</c>, the node degree may exceed the buffer — the caller should fall back
    /// to the linked-list enumerator for correctness.
    /// </summary>
    int ReadEdges(NodeId nodeId, Direction direction, RelationshipTypeId? typeFilter, AdjacencyEntry[] buffer);
}

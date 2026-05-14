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
    /// to the linked-list enumerator (or <see cref="OpenCursor"/>) for correctness.
    /// </summary>
    int ReadEdges(NodeId nodeId, Direction direction, RelationshipTypeId? typeFilter, AdjacencyEntry[] buffer);

    /// <summary>
    /// Open a page-continuation cursor over the adjacency block chain for <paramref name="nodeId"/>.
    /// Unlike <see cref="ReadEdges"/> this never truncates: the cursor walks every page in the
    /// chain and yields entries one at a time, so callers with high-degree nodes never need to
    /// fall back to the linked-list path purely because a fixed-size buffer filled.
    /// Returns an empty cursor when the node has no adjacency block.
    /// </summary>
    AdjacencyCursor OpenCursor(NodeId nodeId, Direction direction, RelationshipTypeId? typeFilter);
}

/// <summary>
/// Streaming cursor over a node's adjacency block chain. Implementations pin one page at
/// a time and advance through linked pages without materialising the full neighbour list.
/// </summary>
public abstract class AdjacencyCursor : IDisposable
{
    /// <summary>Advance to the next matching entry. Returns false when the chain is exhausted.</summary>
    public abstract bool MoveNext();

    /// <summary>Neighbour node id for the current entry. Valid only after <see cref="MoveNext"/> returns true.</summary>
    public abstract NodeId Neighbor { get; }

    /// <summary>Relationship id for the current entry. Valid only after <see cref="MoveNext"/> returns true.</summary>
    public abstract RelationshipId Relationship { get; }

    /// <summary>Relationship type id for the current entry. Valid only after <see cref="MoveNext"/> returns true.</summary>
    public abstract RelationshipTypeId Type { get; }

    public virtual void Dispose() { }

    /// <summary>Singleton empty cursor used when a node has no adjacency block.</summary>
    public static AdjacencyCursor Empty { get; } = new EmptyAdjacencyCursor();

    private sealed class EmptyAdjacencyCursor : AdjacencyCursor
    {
        public override bool MoveNext() => false;
        public override NodeId Neighbor => NodeId.Invalid;
        public override RelationshipId Relationship => RelationshipId.Invalid;
        public override RelationshipTypeId Type => default;
    }
}

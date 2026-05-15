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

    /// <summary>
    /// PW-14 / codex_advice_3 §7.6. Monotonic generation counter for the base
    /// adjacency view; incremented on compact. Stores without a persisted base
    /// return 0.
    /// </summary>
    long Epoch => 0;

    /// <summary>
    /// PW-14: relationship-id watermark recorded at base build time.
    /// Relationships with id &lt; <see cref="BaseRelHwm"/> are part of the
    /// immutable base view; ids &gt;= are post-bulk-load delta records that
    /// live in the relationship linked list. 0 means there is no base.
    /// </summary>
    long BaseRelHwm => 0;

    /// <summary>
    /// PW-14: true when <paramref name="relId"/> belongs to the base view but
    /// has been deleted since the view was built. Expand cursors skip these
    /// entries so deletes are visible without rebuilding the base.
    /// </summary>
    bool IsTombstoned(RelationshipId relId) => false;

    /// <summary>
    /// PW-14: mark a base relationship as deleted. No-op when
    /// <c>relId.Value &gt;= <see cref="BaseRelHwm"/></c> — delta deletes only
    /// need the linked-list unlink that <c>RelationshipStore.Delete</c> already
    /// performs.
    /// </summary>
    void Tombstone(RelationshipId relId) { }
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

    /// <summary>
    /// BA-6: raw 64-bit payload for the current entry when the cursor is opened on
    /// an <see cref="AdjacencyBlockStoreV2"/> with a payload lane. V1 cursors and
    /// V2 cursors built without a payload lane return 0. Reinterpret as
    /// <c>double</c> via <see cref="BitConverter.Int64BitsToDouble"/> when the
    /// store's <see cref="PayloadKind"/> is <see cref="PayloadKind.Double"/>.
    /// </summary>
    public virtual long WeightRaw => 0;

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

/// <summary>
/// BA-6: optional extension contract for adjacency stores that carry an inline
/// payload lane (edge weight or similar scalar). Operators can probe for this
/// via <c>tx.AdjacencyBlocks as IAdjacencyPayloadView</c> and choose a
/// <see cref="Quiver.Operators.ExpandOutputMode.NeighborAndWeight"/> projection
/// without going through the property chain.
/// </summary>
public interface IAdjacencyPayloadView
{
    /// <summary>The payload spec fixed at view-build time. Returned by reference value.</summary>
    PayloadLaneSpec PayloadSpec { get; }
}

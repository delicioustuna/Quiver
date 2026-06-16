namespace Quiver.Query.Physical;

internal enum ExpandOutputMode
{
    NeighborOnly = 1,
    NeighborAndRel = 2,
    Full = 3,
    /// <summary>
    /// BA-6: emit (rel, neighbor, weight) where the
    /// weight is read from the V2 adjacency view's inline payload lane. The
    /// weight slot is typed according to the active <c>PayloadLaneSpec.Kind</c>
    /// (Int64 → <see cref="TupleSlotType.Int64"/>, Double →
    /// <see cref="TupleSlotType.Double"/>). When the underlying
    /// <c>IAdjacencyBlockStore</c> does not expose a payload lane the slot
    /// carries the lane's <c>DefaultRaw</c> — never the property-chain value
    /// — so callers get a predictable contract regardless of build mode.
    /// </summary>
    NeighborAndWeight = 4,
}

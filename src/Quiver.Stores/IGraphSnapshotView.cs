using Quiver.Core;

namespace Quiver.Stores;

/// <summary>
/// PW-15 / codex_advice_3 §7.7. Algorithm-oriented read-only graph snapshot.
/// Materialises the current adjacency state into compressed sparse row (CSR
/// for outgoing) and compressed sparse column (CSC for incoming) arrays so
/// repeated multi-pass algorithms (PageRank, Louvain, repeated BFS / shortest
/// path) can iterate neighbors as a flat <see cref="ReadOnlySpan{T}"/> instead
/// of opening a per-call cursor over the page-chained adjacency blocks.
///
/// The view is point-in-time — snapshot transactions read this safely without
/// blocking writers, but mutations after construction are invisible. Dispose
/// to release the underlying arrays back to <see cref="System.Buffers.ArrayPool{T}"/>.
///
/// <see cref="Epoch"/> mirrors <see cref="IAdjacencyBlockStore.Epoch"/> at the
/// time of construction so callers can detect that a compact has invalidated
/// any cached numeric results computed against this view.
/// </summary>
public interface IGraphSnapshotView : IDisposable
{
    /// <summary>Adjacency epoch captured at snapshot build time. 0 when no base view exists.</summary>
    long Epoch { get; }

    /// <summary>
    /// Number of node slots indexed by this view. Node ids in
    /// <c>[0, NodeCount)</c> are addressable; ids outside the range return
    /// empty spans / zero degree.
    /// </summary>
    long NodeCount { get; }

    /// <summary>Total live relationships materialised into the view.</summary>
    long EdgeCount { get; }

    /// <summary>True when the view carries an inline weight lane; <see cref="WeightBitsOut"/> returns empty otherwise.</summary>
    bool HasWeights { get; }

    /// <summary>Out-degree for <paramref name="nodeId"/>. Returns 0 when out of range.</summary>
    int OutDegree(NodeId nodeId);

    /// <summary>In-degree for <paramref name="nodeId"/>. Returns 0 when out of range.</summary>
    int InDegree(NodeId nodeId);

    /// <summary>Outgoing neighbour ids (target of each out-edge) for <paramref name="nodeId"/>.</summary>
    ReadOnlySpan<long> OutNeighbors(NodeId nodeId);

    /// <summary>Incoming neighbour ids (source of each in-edge) for <paramref name="nodeId"/>.</summary>
    ReadOnlySpan<long> InNeighbors(NodeId nodeId);

    /// <summary>Outgoing relationship ids parallel to <see cref="OutNeighbors"/>.</summary>
    ReadOnlySpan<long> OutRelationshipIds(NodeId nodeId);

    /// <summary>Incoming relationship ids parallel to <see cref="InNeighbors"/>.</summary>
    ReadOnlySpan<long> InRelationshipIds(NodeId nodeId);

    /// <summary>
    /// Raw 64-bit weight payload parallel to <see cref="OutNeighbors"/>. Reinterpret
    /// via <see cref="BitConverter.Int64BitsToDouble"/> when the source store
    /// payload kind was <see cref="PayloadKind.Double"/>. Empty when
    /// <see cref="HasWeights"/> is false.
    /// </summary>
    ReadOnlySpan<long> WeightBitsOut(NodeId nodeId);
}

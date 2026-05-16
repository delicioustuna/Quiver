using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// PW-13 / codex_advice_3.md §7.8: Callback contract used by BFS-style algorithms
/// (<see cref="BfsOperator"/>, <see cref="VariableLengthExpandOperator"/>,
/// <see cref="ShortestPathOperator"/>, <see cref="ParallelBfsOperator"/>) to share
/// the per-neighbor visit logic while letting each operator own its own state
/// shape (frontier queue, visited set, distance map…).
/// </summary>
/// <remarks>
/// The kernel only describes <c>what to do per neighbor</c>. Frontier scheduling
/// and row materialisation stay with the operator so that incremental
/// (Volcano) iteration keeps working without coroutines. Physical access path
/// selection (adjacency block / linked-list / relationship scan) is hidden in
/// <see cref="IGraphAccessMethods.Expand"/>; <see cref="OneHopExpansion"/> is
/// the wrapper kernels call to walk one hop.
/// </remarks>
public interface IGraphKernel<TState>
{
    /// <summary>
    /// Called once per source before any hop is expanded. Implementations seed
    /// the frontier / visited set inside <paramref name="state"/>.
    /// </summary>
    void Initialize(NodeId source, ref TState state);

    /// <summary>
    /// Invoked for every neighbour edge yielded by <see cref="OneHopExpansion.Expand"/>.
    /// Return <c>true</c> to keep walking the cursor, <c>false</c> to abort the
    /// remainder of the current hop (e.g. shortest-path target reached).
    /// </summary>
    /// <param name="weightRaw">
    /// Raw 64-bit payload from <see cref="ExpandCursor.WeightRaw"/>. Cursors
    /// without a payload lane forward 0; kernels that do not need edge weight
    /// can ignore it (the cost is one virtual call to <c>WeightRaw</c>, which
    /// the JIT already inlines for the common no-payload backends).
    /// </param>
    bool VisitNeighbor(
        NodeId source,
        NodeId target,
        RelationshipId relationshipId,
        long weightRaw,
        int depth,
        ref TState state);

    /// <summary>
    /// Asked between hops to decide whether to dequeue the next frontier slot.
    /// <paramref name="depth"/> is the depth of the slot about to be expanded
    /// (so <c>0</c> means the source itself).
    /// </summary>
    bool ShouldContinue(int depth, in TState state);
}

/// <summary>
/// PW-13 / codex_advice_3.md §7.8: Single-hop expansion primitive shared by
/// BFS-style operators. Wraps <see cref="IGraphAccessMethods.Expand"/> so the
/// kernel never touches the underlying cursor; that lets the same algorithm
/// shell run over the binary backend's adjacency-block / linked-list path,
/// the SQLite backend's index path, or a future CSR snapshot view without
/// changes to the kernel.
/// </summary>
public static class OneHopExpansion
{
    /// <summary>
    /// Walk all edges incident to <paramref name="source"/> in
    /// <paramref name="direction"/> (optionally filtered by
    /// <paramref name="typeFilter"/>) and call
    /// <see cref="IGraphKernel{TState}.VisitNeighbor"/> for each neighbour.
    /// Returns <c>false</c> when the kernel asked to stop early; callers may
    /// use this signal to short-circuit the outer frontier loop (shortest path).
    /// </summary>
    public static bool Expand<TState>(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter,
        int depth,
        IGraphKernel<TState> kernel,
        ref TState state)
    {
        using var cursor = tx.Access.Expand(tx, source, direction, typeFilter);
        while (cursor.MoveNext())
        {
            if (!kernel.VisitNeighbor(
                    source,
                    cursor.Neighbor,
                    cursor.Relationship,
                    cursor.WeightRaw,
                    depth,
                    ref state))
                return false;
        }
        return true;
    }
}

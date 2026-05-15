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

    /// <summary>
    /// VEC-5: KNN access path. Delegates to the backend's <see cref="IVectorStore"/>
    /// so operators can treat vector search as a first-class scan source. The
    /// query span is copied internally — callers do not need to keep it alive
    /// past the call.
    /// </summary>
    /// <remarks>
    /// codex_advice_3.md §6.4. Returns results in descending similarity order
    /// (Cosine/Dot) or ascending distance (Euclidean — internally negated so
    /// the cursor's score is still "higher = closer"). Backends without a
    /// vector store should throw <see cref="NotSupportedException"/>.
    /// </remarks>
    VectorSearchCursor KnnSearch(string indexName, ReadOnlySpan<float> query, int k)
        => throw new NotSupportedException(
            "This backend does not implement KnnSearch. Wire an IVectorStore into the access methods.");

    /// <summary>
    /// VEC-6: filtered KNN. Returns the top-<paramref name="k"/> vectors that
    /// are also members of <paramref name="candidates"/>. The default
    /// implementation oversamples <see cref="KnnSearch"/> (k → 2k → 4k …) and
    /// post-filters until either k matches are found or the oversample cap
    /// is reached — backends may pushdown the filter into the vector index
    /// when the underlying ANN structure supports it.
    /// </summary>
    /// <remarks>
    /// codex_advice_3.md §6.4. Score ordering is preserved: results come back
    /// in descending similarity. <paramref name="candidates"/> with a
    /// different <see cref="EntityCandidateSet.Kind"/> than the index's
    /// <see cref="EntityKind"/> match nothing — that's a configuration error
    /// the operator level surfaces, not a contract violation.
    /// </remarks>
    VectorSearchCursor KnnSearchFiltered(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        EntityCandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");

        // Empty candidate set ⇒ empty result. Avoid touching the vector index
        // for the trivial case where graph-first already pruned to nothing.
        if (candidates.Count == 0)
            return EmptyVectorSearchCursor.Instance;

        // Two-stage oversample. The cap is generous: max(k*64, candidates.Count*2)
        // — high enough that even adversarial layouts converge, while still
        // bounding worst-case work below "score every vector twice".
        int oversampleCap = Math.Max(k * 64, candidates.Count * 2);
        int candidateK = Math.Min(Math.Max(k * 4, k + candidates.Count / 4), oversampleCap);

        while (true)
        {
            var hits = new List<VectorSearchResult>(k);
            using (var cursor = KnnSearch(indexName, query, candidateK))
            {
                while (cursor.MoveNext())
                {
                    var hit = cursor.Current;
                    if (!candidates.Contains(hit.EntityKind, hit.EntityId)) continue;
                    hits.Add(hit);
                    if (hits.Count >= k) break;
                }
            }

            // Either we hit k or we've exhausted the index (oversample at cap
            // and still short). Both paths return the partial result.
            if (hits.Count >= k || candidateK >= oversampleCap)
                return new MaterializedVectorSearchCursor(hits);

            candidateK = Math.Min(candidateK * 2, oversampleCap);
        }
    }
}

internal sealed class EmptyVectorSearchCursor : VectorSearchCursor
{
    public static readonly EmptyVectorSearchCursor Instance = new();
    public override bool MoveNext() => false;
    public override VectorSearchResult Current
        => throw new InvalidOperationException("Cursor is empty.");
}

internal sealed class MaterializedVectorSearchCursor(IReadOnlyList<VectorSearchResult> hits) : VectorSearchCursor
{
    private int _i = -1;
    public override bool MoveNext() => ++_i < hits.Count;
    public override VectorSearchResult Current => hits[_i];
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

    /// <summary>
    /// BA-6: raw 64-bit payload (typically an edge weight) for the current
    /// edge. Cursors backed by a V2 adjacency view forward the inline payload
    /// lane; other cursors return 0. Reinterpret as <see cref="double"/> via
    /// <see cref="BitConverter.Int64BitsToDouble"/> when the active payload
    /// kind is Double.
    /// </summary>
    public virtual long WeightRaw => 0;

    public virtual void Dispose() { }
}

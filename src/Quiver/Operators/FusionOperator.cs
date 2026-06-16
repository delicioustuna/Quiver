using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// FTS-5 operator: fuses the ranked NodeId streams of two or more child operators
/// (typically <see cref="FullTextScanOperator"/> BM25 + <see cref="KnnNodeSourceOperator"/>)
/// with Reciprocal Rank Fusion and emits the top-<c>k</c> in fused order.
/// </summary>
/// <remarks>
/// RRF score is <c>Σ_i 1/(k0 + rank_i(d))</c> with k0=60 and
/// rank 1-based within each child stream — it depends only on rank, so the existing
/// "score is not surfaced" policy of the KNN / BM25 leaves is preserved (no score
/// plumbing). Each child is drained fully on <see cref="Open"/> (k is a few dozen, so
/// memory is a non-issue); ties on the fused score break by ascending entity id for a
/// deterministic order. Visibility is already enforced by the children, so the fused
/// ids are emitted as-is.
/// <para>
/// Precondition: every child must emit ids in the same packed <see cref="NodeId"/> space
/// so the per-child accumulators key on the same entity. The current children (text-first
/// BM25 + vector-first KNN) satisfy this; a future graph-first child (FTS-4 family) must
/// keep emitting node ids in that space rather than candidate-local handles.
/// </para>
/// </remarks>
internal sealed class FusionOperator : IPhysicalOperator
{
    private const double K0 = 60.0;

    private readonly IPhysicalOperator[] _children;
    private readonly int[] _childColumns;
    private readonly int _k;
    private long[] _results = Array.Empty<long>();
    private int _pos = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FusionOperator(IPhysicalOperator[] children, int[] childColumns, int k)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(childColumns);
        if (children.Length == 0)
            throw new ArgumentException("Fusion requires at least one child operator.", nameof(children));
        if (children.Length != childColumns.Length)
            throw new ArgumentException("children / childColumns length mismatch.", nameof(childColumns));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        _children = children;
        _childColumns = childColumns;
        _k = k;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        // RRF accumulation: for each child, walk its ranked stream and add 1/(k0 + rank)
        // (rank 1-based) to the entity's running score. A doc that places well in more
        // than one child is boosted; a doc seen in only one child still ranks on its own.
        var scores = new Dictionary<long, double>();
        for (int c = 0; c < _children.Length; c++)
        {
            var child = _children[c];
            int col = _childColumns[c];
            child.Open(tx);
            int rank = 0;
            while (child.MoveNext())
            {
                var slot = child.Current[col];
                if (slot.Type != TupleSlotType.NodeId) continue;
                rank++;
                long id = slot.LongValue;
                double contrib = 1.0 / (K0 + rank);
                scores[id] = scores.TryGetValue(id, out var prev) ? prev + contrib : contrib;
            }
        }

        var ranked = new List<KeyValuePair<long, double>>(scores);
        ranked.Sort(static (a, b) =>
        {
            int cmp = b.Value.CompareTo(a.Value);
            return cmp != 0 ? cmp : a.Key.CompareTo(b.Key);
        });
        _results = ranked.Take(_k).Select(kv => kv.Key).ToArray();
        _pos = -1;
    }

    public bool MoveNext()
    {
        if (_pos + 1 >= _results.Length) return false;
        _pos++;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _results[_pos] };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose()
    {
        foreach (var child in _children)
            child.Dispose();
    }
}

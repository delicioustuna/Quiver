using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// VEC-5 leaf operator: streams the top-<c>k</c> node ids from a vector index
/// in descending similarity order. Acts as a scan source so it composes with
/// the existing <see cref="FilterOperator"/> / <see cref="ExpandOperator"/>
/// chain — callers wire it as the first stage of a traversal, then apply
/// label / property / expand stages on top.
/// </summary>
/// <remarks>
/// codex_advice_3.md §6.4. Score is intentionally not surfaced in this MVP;
/// users who need it call <c>db.Vectors.KnnSearch</c> directly. Indexes bound
/// to <see cref="EntityKind.Relationship"/> are rejected — relationship-KNN
/// will land as a sibling operator when there is demand.
/// </remarks>
public sealed class KnnNodeSourceOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly float[] _query;
    private readonly int _k;
    private VectorSearchCursor? _cursor;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public KnnNodeSourceOperator(string indexName, ReadOnlySpan<float> query, int k)
    {
        if (string.IsNullOrEmpty(indexName))
            throw new ArgumentException("Vector index name must not be empty.", nameof(indexName));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        _indexName = indexName;
        _query = query.ToArray();
        _k = k;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _cursor = tx.Access.KnnSearch(_indexName, _query, _k);
    }

    public bool MoveNext()
    {
        while (_cursor!.MoveNext())
        {
            var hit = _cursor.Current;
            // Skip non-Node results so a mistakenly-typed index doesn't poison
            // the traversal — but if every result is non-Node we want to surface
            // that with an empty stream rather than throw.
            if (hit.EntityKind != EntityKind.Node) continue;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = hit.EntityId };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose() => _cursor?.Dispose();
}

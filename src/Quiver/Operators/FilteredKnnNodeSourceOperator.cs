using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// VEC-6 leaf-ish operator: drains an upstream NodeId-producing operator into
/// an <see cref="EntityCandidateSet"/>, then asks
/// <see cref="IGraphAccessMethods.KnnSearchFiltered"/> for the top-<c>k</c>
/// vectors that fall inside that set. Acts as the graph-first arm — the
/// upstream operator usually a label or property filter on
/// <see cref="AllNodesScanOperator"/>.
/// </summary>
/// <remarks>
/// codex_advice_3.md §6.4. Pairs with <see cref="KnnNodeSourceOperator"/>
/// (vector-first); the <c>QueryOptimizer.ChooseKnnStrategy</c> decides which
/// arm to build. Like the unfiltered operator, score is intentionally not
/// surfaced in the tuple stream; callers can hold a reference to the access
/// method directly when raw scores are needed.
/// </remarks>
public sealed class FilteredKnnNodeSourceOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly string _indexName;
    private readonly float[] _query;
    private readonly int _k;
    private VectorSearchCursor? _cursor;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FilteredKnnNodeSourceOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        string indexName,
        ReadOnlySpan<float> query,
        int k)
    {
        if (string.IsNullOrEmpty(indexName))
            throw new ArgumentException("Vector index name must not be empty.", nameof(indexName));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceNodeColumn = sourceNodeColumn;
        _indexName = indexName;
        _query = query.ToArray();
        _k = k;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _source.Open(tx);
        var ids = new List<long>();
        while (_source.MoveNext())
        {
            var slot = _source.Current[_sourceNodeColumn];
            if (slot.Type == TupleSlotType.NodeId) ids.Add(slot.LongValue);
        }
        var candidates = new EntityCandidateSet(EntityKind.Node, ids);
        _cursor = tx.Access.KnnSearchFiltered(_indexName, _query, _k, candidates);
    }

    public bool MoveNext()
    {
        while (_cursor!.MoveNext())
        {
            var hit = _cursor.Current;
            if (hit.EntityKind != EntityKind.Node) continue;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = hit.EntityId };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        _cursor?.Dispose();
        _source.Dispose();
    }
}

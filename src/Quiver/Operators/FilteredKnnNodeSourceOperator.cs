using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 上流の NodeId 生成演算子を <see cref="EntityCandidateSet"/> に排出し、
/// <see cref="IGraphAccessMethods.KnnSearchFiltered"/> でそのセット内の
/// top-<c>k</c> ベクトルを取得する graph-first KNN 演算子。
/// 上流は通常 <see cref="AllNodesScanOperator"/> にラベル / プロパティフィルタを適用したもの。
/// </summary>
/// <remarks>
/// <see cref="KnnNodeSourceOperator"/> (vector-first) と対をなし、
/// <c>QueryOptimizer.ChooseKnnStrategy</c> がどちらを構築するか決定する。
/// フィルタなし版と同様、スコアはタプルストリームに意図的に非公開。
/// </remarks>
internal sealed class FilteredKnnNodeSourceOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly string _indexName;
    private readonly float[] _query;
    private readonly int _k;
    private readonly VectorSearchOptions? _options;
    private VectorSearchCursor? _cursor;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FilteredKnnNodeSourceOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
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
        _options = options;
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
        _cursor = tx.Access.KnnSearchFiltered(_indexName, _query, _k, candidates, _options);
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

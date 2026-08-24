using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// 上流の VertexId 生成演算子を full typed owner の集合に排出し、
/// <see cref="IGraphAccessMethods.KnnSearchFiltered"/> でそのセット内の
/// top-<c>k</c> ベクトルを取得する graph-first KNN 演算子。
/// 上流は通常 <see cref="AllVerticesScanOperator"/> にラベル / プロパティフィルタを適用したもの。
/// </summary>
/// <remarks>
/// <see cref="KnnVertexSourceOperator"/> (vector-first) と対をなし、
/// <c>QueryOptimizer.ChooseKnnStrategy</c> がどちらを構築するか決定する。
/// フィルタなし版と同様、スコアはタプルストリームに意図的に非公開。
/// </remarks>
internal sealed class FilteredKnnVertexSourceOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceVertexColumn;
    private readonly string _indexName;
    private readonly float[] _query;
    private readonly int _k;
    private readonly VectorSearchOptions? _options;
    private VectorSearchCursor? _cursor;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FilteredKnnVertexSourceOperator(
        IPhysicalOperator source,
        int sourceVertexColumn,
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
        _sourceVertexColumn = sourceVertexColumn;
        _indexName = indexName;
        _query = query.ToArray();
        _k = k;
        _options = options;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _source.Open(tx);
        var candidates = new HashSet<EntityRef>();
        while (_source.MoveNext())
        {
            var slot = _source.Current[_sourceVertexColumn];
            if (slot.Type != TupleSlotType.VertexId)
                continue;

            using var vertex = tx.Vertices.Read(new VertexId(slot.LongValue));
            if (vertex.InUse)
                candidates.Add(EntityRef.From(vertex.Id));
        }
        _cursor = tx.Access.KnnSearchFiltered(
            tx,
            _indexName,
            _query,
            _k,
            candidates,
            _options);
    }

    public bool MoveNext()
    {
        while (_cursor!.MoveNext())
        {
            var hit = _cursor.Current;
            if (hit.EntityKind != EntityKind.Vertex) continue;
            _buffer[0] = new TupleSlot
            {
                Type = TupleSlotType.VertexId,
                LongValue = hit.Owner.Value,
            };
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

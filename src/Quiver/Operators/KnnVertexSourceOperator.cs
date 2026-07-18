using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// ベクトルインデックスから類似度降順で上位 <c>k</c> 件の VertexId を列挙するリーフオペレータ。
/// スキャンソースとして動作し、後段の <see cref="FilterOperator"/> / <see cref="ExpandOperator"/>
/// チェーンと合成できる。
/// </summary>
/// <remarks>
/// スコアは現時点では公開しない。必要な場合は read transaction の
/// <c>KnnSearch</c> を使う。
/// <see cref="EntityKind.Edge"/> にバインドされたインデックスは拒否する —
/// edge-KNN は需要が生じた時点で兄弟オペレータとして追加する。
/// </remarks>
internal sealed class KnnVertexSourceOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly float[] _query;
    private readonly int _k;
    private readonly VectorSearchOptions? _options;
    private VectorSearchCursor? _cursor;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public KnnVertexSourceOperator(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
    {
        if (string.IsNullOrEmpty(indexName))
            throw new ArgumentException("Vector index name must not be empty.", nameof(indexName));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
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
        _tx = tx;
        _cursor = tx.Access.KnnSearch(tx, _indexName, _query, _k, _options);
    }

    public bool MoveNext()
    {
        while (_cursor!.MoveNext())
        {
            var hit = _cursor.Current;
            // Vertex 以外の結果はスキップする。誤った型のインデックスが走査を汚染しないための防御で、
            // 全件 Vertex 以外の場合は例外ではなく空ストリームを返す。
            if (hit.EntityKind != EntityKind.Vertex) continue;

            // vector index は physical Sequence を返す。Fusion などの論理演算子へ渡す前に
            // full VertexId を復元し、無効化済みの candidate は出力しない。
            var materializer = new EntityIdentityMaterializer(_tx!.Vertices);
            if (!materializer.TryVertex(new VertexId(hit.EntityId), out var logical))
                continue;

            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = logical.Value };
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
        _tx = null;
    }
}

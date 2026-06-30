using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// ベクトルインデックスから類似度降順で上位 <c>k</c> 件の NodeId を列挙するリーフオペレータ。
/// スキャンソースとして動作し、後段の <see cref="FilterOperator"/> / <see cref="ExpandOperator"/>
/// チェーンと合成できる。
/// </summary>
/// <remarks>
/// スコアは現時点では公開しない。必要な場合は <c>db.Vectors.KnnSearch</c> を直接呼ぶ。
/// <see cref="EntityKind.Relationship"/> にバインドされたインデックスは拒否する —
/// relationship-KNN は需要が生じた時点で兄弟オペレータとして追加する。
/// </remarks>
internal sealed class KnnNodeSourceOperator : IPhysicalOperator
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
            // Node 以外の結果はスキップする。誤った型のインデックスが走査を汚染しないための防御で、
            // 全件 Node 以外の場合は例外ではなく空ストリームを返す。
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

using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 全Vertexをスキャンし VertexId を 1 行ずつ放出するソース演算子。
/// オプションのラベルフィルタ付き。
/// </summary>
internal sealed class AllVerticesScanOperator : IPhysicalOperator
{
    private readonly LabelId? _filterLabel;
    private IEnumerator<VertexId>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public AllVerticesScanOperator(LabelId? filterLabel = null) => _filterLabel = filterLabel;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _enumerator = tx.Access.ScanVertices(tx, _filterLabel).GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator!.MoveNext())
        {
            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _enumerator.Current.Value };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose() => _enumerator?.Dispose();
}

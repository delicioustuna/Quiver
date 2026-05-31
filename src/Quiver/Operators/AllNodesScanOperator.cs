using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Operators;

public sealed class AllNodesScanOperator : IPhysicalOperator
{
    private readonly LabelId? _filterLabel;
    private IEnumerator<NodeId>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public AllNodesScanOperator(LabelId? filterLabel = null) => _filterLabel = filterLabel;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _enumerator = tx.Access.ScanNodes(tx, _filterLabel).GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator!.MoveNext())
        {
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _enumerator.Current.Value };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose() => _enumerator?.Dispose();
}

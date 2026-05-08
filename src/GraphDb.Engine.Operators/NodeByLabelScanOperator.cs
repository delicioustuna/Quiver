using GraphDb.Engine.Core;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class NodeByLabelScanOperator : IPhysicalOperator
{
    private readonly LabelId _labelId;
    private ITransaction? _tx;
    private IEnumerator<NodeId>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public NodeByLabelScanOperator(LabelId labelId) => _labelId = labelId;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _enumerator = tx.Nodes.Scan().GetEnumerator();
    }

    public bool MoveNext()
    {
        while (_enumerator!.MoveNext())
        {
            var nodeId = _enumerator.Current;
            var h = _tx!.Nodes.Read(nodeId);
            if (h.Label != _labelId) continue;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = nodeId.Value };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose() => _enumerator?.Dispose();
}

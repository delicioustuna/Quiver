using GraphDb.Engine.Core;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class AllNodesScanOperator : IPhysicalOperator
{
    private readonly LabelId? _filterLabel;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public AllNodesScanOperator(LabelId? filterLabel = null) => _filterLabel = filterLabel;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) => _tx = tx;
    public bool MoveNext() => throw new NotImplementedException();
    public void Dispose() { }
}

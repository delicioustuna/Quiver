using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class NodeIndexRangeScanOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly ITupleProvider _fromProvider;
    private readonly bool _fromInclusive;
    private readonly ITupleProvider _toProvider;
    private readonly bool _toInclusive;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public NodeIndexRangeScanOperator(
        string indexName,
        ITupleProvider fromProvider, bool fromInclusive,
        ITupleProvider toProvider, bool toInclusive)
    {
        _indexName = indexName;
        _fromProvider = fromProvider;
        _fromInclusive = fromInclusive;
        _toProvider = toProvider;
        _toInclusive = toInclusive;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) => _tx = tx;
    public bool MoveNext() => throw new NotImplementedException();
    public void Dispose() { }
}

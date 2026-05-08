using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class NodeIndexSeekOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly ITupleProvider _keyProvider;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public NodeIndexSeekOperator(string indexName, ITupleProvider keyProvider)
    {
        _indexName = indexName;
        _keyProvider = keyProvider;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) => _tx = tx;
    public bool MoveNext() => throw new NotImplementedException();
    public void Dispose() { }
}

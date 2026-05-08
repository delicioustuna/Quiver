using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class ExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly ExpandOutputMode _outputMode;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer;

    public ExpandOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        ExpandOutputMode outputMode)
    {
        _source = source;
        _sourceNodeColumn = sourceNodeColumn;
        _direction = direction;
        _typeFilter = typeFilter;
        _outputMode = outputMode;
        _buffer = new TupleSlot[outputMode == ExpandOutputMode.Full ? 3 : outputMode == ExpandOutputMode.NeighborAndRel ? 2 : 1];
    }

    public TupleSchema Schema => throw new NotImplementedException();
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _tx = tx; _source.Open(tx); }
    public bool MoveNext() => throw new NotImplementedException();
    public void Dispose() { _source.Dispose(); }
}

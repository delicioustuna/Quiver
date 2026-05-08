using GraphDb.Engine.Core;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class PropertyLookupOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _entityIdColumn;
    private readonly PropertyKeyId _keyId;
    private readonly string _outputColumnName;
    private ITransaction? _tx;
    private TupleSlot[]? _buffer;

    public PropertyLookupOperator(
        IPhysicalOperator source,
        int entityIdColumn,
        PropertyKeyId keyId,
        string outputColumnName)
    {
        _source = source;
        _entityIdColumn = entityIdColumn;
        _keyId = keyId;
        _outputColumnName = outputColumnName;
    }

    public TupleSchema Schema => throw new NotImplementedException();
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer!);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _buffer = new TupleSlot[_source.Schema.Columns.Count + 1];
    }

    public bool MoveNext() => throw new NotImplementedException();
    public void Dispose() { _source.Dispose(); }
}

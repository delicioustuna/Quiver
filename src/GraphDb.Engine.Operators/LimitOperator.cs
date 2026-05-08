using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class LimitOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly long _limit;
    private readonly long _skip;
    private ITransaction? _tx;
    private long _skipped;
    private long _produced;

    public LimitOperator(IPhysicalOperator source, long limit, long skip = 0)
    {
        _source = source;
        _limit = limit;
        _skip = skip;
    }

    public TupleSchema Schema => _source.Schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => _source.Current;

    public void Open(ITransaction tx) { _tx = tx; _source.Open(tx); }
    public bool MoveNext() => throw new NotImplementedException();
    public void Dispose() { _source.Dispose(); }
}

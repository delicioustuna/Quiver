using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class FilterOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly IPredicate _predicate;
    private ITransaction? _tx;

    public FilterOperator(IPhysicalOperator source, IPredicate predicate)
    {
        _source = source;
        _predicate = predicate;
    }

    public TupleSchema Schema => _source.Schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => _source.Current;

    public void Open(ITransaction tx) { _tx = tx; _source.Open(tx); }

    public bool MoveNext()
    {
        while (_source.MoveNext())
        {
            var cur = _source.Current;
            if (_predicate.Evaluate(in cur, _tx!))
            {
                var s = Statistics;
                s.RowsProduced++;
                Statistics = s;
                return true;
            }
        }
        return false;
    }

    public ReadOnlySpan<byte> GetBytes(int column) => _source.GetBytes(column);

    public void Dispose()
    {
        _source.Dispose();
        if (_predicate is IDisposable d) d.Dispose();
    }
}

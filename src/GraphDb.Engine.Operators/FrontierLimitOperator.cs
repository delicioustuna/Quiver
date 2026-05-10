using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

/// <summary>
/// Limits the number of rows emitted per BFS depth level.
/// Designed to wrap BfsOperator (which outputs a depth column as Int64).
/// Within each distinct depth value, at most <paramref name="maxFrontierSize"/> rows pass through.
/// Rows beyond the limit at a given depth are silently dropped.
/// </summary>
public sealed class FrontierLimitOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _depthColumn;
    private readonly long _maxFrontierSize;
    private ITransaction? _tx;
    private long _currentDepth;
    private long _countAtCurrentDepth;

    public FrontierLimitOperator(IPhysicalOperator source, int depthColumn, long maxFrontierSize)
    {
        if (maxFrontierSize < 1) throw new ArgumentOutOfRangeException(nameof(maxFrontierSize));
        _source = source;
        _depthColumn = depthColumn;
        _maxFrontierSize = maxFrontierSize;
    }

    public TupleSchema Schema => _source.Schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => _source.Current;

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _currentDepth = -1;
        _countAtCurrentDepth = 0;
    }

    public bool MoveNext()
    {
        while (_source.MoveNext())
        {
            long depth = _source.Current[_depthColumn].LongValue;

            if (depth != _currentDepth)
            {
                _currentDepth = depth;
                _countAtCurrentDepth = 0;
            }

            if (_countAtCurrentDepth < _maxFrontierSize)
            {
                _countAtCurrentDepth++;
                var s = Statistics;
                s.RowsProduced++;
                Statistics = s;
                return true;
            }
            // Frontier limit reached at this depth — skip row.
        }
        return false;
    }

    public ReadOnlySpan<byte> GetBytes(int column) => _source.GetBytes(column);
    public void Dispose() => _source.Dispose();
}

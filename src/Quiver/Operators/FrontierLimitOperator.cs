using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// BFS の深さレベルごとに放出する行数を制限する。
/// BfsOperator (深さ列を Int64 で出力する) をラップする想定。
/// 同一深さ値の範囲内では最大 <c>maxFrontierSize</c> 件だけが通過し、
/// それを超えた行は黙ってドロップされる。
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
            // この深さでフロンティア上限に達した — 行をスキップする。
        }
        return false;
    }

    public ReadOnlySpan<byte> GetBytes(int column) => _source.GetBytes(column);
    public void Dispose() => _source.Dispose();
}

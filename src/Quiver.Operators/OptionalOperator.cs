using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// GC-4: Gremlin <c>.optional(t)</c> / Cypher <c>OPTIONAL MATCH</c>. For each
/// input row, evaluate the branch against a freshly bound
/// <see cref="CorrelatedInputOperator"/>. If the branch produces at least one
/// row, drain it; otherwise emit the input row's entity column once so that the
/// upstream value flows through with no left-side rows dropped.
///
/// The branch must produce a single-column NodeId tuple; the fall-through
/// shape is the source's entity slot copied verbatim, so the output column is
/// always a single NodeId.
/// </summary>
public sealed class OptionalOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly CorrelatedInputOperator _probe;
    private readonly IPhysicalOperator _branch;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("optional", TupleSlotType.NodeId)]);

    private ITransaction? _tx;
    private bool _hasSource;
    private bool _branchEmitted;
    private TupleSlot _seed;
    private bool _emitFallthrough;

    public OptionalOperator(
        IPhysicalOperator source,
        int sourceColumn,
        CorrelatedInputOperator probe,
        IPhysicalOperator branch)
    {
        _source = source;
        _srcCol = sourceColumn;
        _probe = probe;
        _branch = branch;
    }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _hasSource = false;
        _branchEmitted = false;
        _emitFallthrough = false;
    }

    public bool MoveNext()
    {
        while (true)
        {
            if (_emitFallthrough)
            {
                _emitFallthrough = false;
                _hasSource = false;
                _buffer[0] = _seed;
                var s = Statistics; s.RowsProduced++; Statistics = s;
                return true;
            }

            if (_hasSource)
            {
                if (_branch.MoveNext())
                {
                    _buffer[0] = _branch.Current[0];
                    _branchEmitted = true;
                    var s = Statistics; s.RowsProduced++; Statistics = s;
                    return true;
                }
                if (!_branchEmitted) _emitFallthrough = true;
                else _hasSource = false;
                continue;
            }

            if (!_source.MoveNext()) return false;
            _seed = _source.Current[_srcCol];
            _probe.Bind(_seed);
            _branch.Open(_tx!);
            _hasSource = true;
            _branchEmitted = false;
        }
    }

    public void Dispose()
    {
        _branch.Dispose();
        _source.Dispose();
    }
}

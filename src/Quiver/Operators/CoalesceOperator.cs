using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 各入力行に対して分岐を順番に試し、
/// 最初に行を生成した分岐の残りを使い切ったら、それ以降の分岐はスキップする。
/// 全分岐が空だった入力行は出力に寄与しない。
/// すべての分岐は単一列 VertexId のタプルを返す必要がある。
/// </summary>
internal sealed class CoalesceOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly CorrelatedInputOperator[] _probes;
    private readonly IPhysicalOperator[] _branches;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("coalesce", TupleSlotType.VertexId)]);

    private ITransaction? _tx;
    private int _curBranch = -1;
    private bool _hasSource;
    private bool _branchEmitted;
    private TupleSlot _seed;

    public CoalesceOperator(
        IPhysicalOperator source,
        int sourceColumn,
        CorrelatedInputOperator[] probes,
        IPhysicalOperator[] branches)
    {
        if (probes is null || branches is null) throw new ArgumentNullException();
        if (probes.Length != branches.Length || branches.Length == 0)
            throw new ArgumentException("Coalesce には少なくとも 1 組、かつ同数の (probe, branch) ペアが必要です。");
        _source = source;
        _srcCol = sourceColumn;
        _probes = probes;
        _branches = branches;
    }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _hasSource = false;
        _curBranch = -1;
        _branchEmitted = false;
    }

    public bool MoveNext()
    {
        while (true)
        {
            if (_hasSource && _curBranch >= 0 && _curBranch < _branches.Length)
            {
                if (_branches[_curBranch].MoveNext())
                {
                    _buffer[0] = _branches[_curBranch].Current[0];
                    _branchEmitted = true;
                    var s = Statistics; s.RowsProduced++; Statistics = s;
                    return true;
                }

                // 現在の分岐を使い切った。
                if (_branchEmitted)
                {
                    _hasSource = false;
                    continue;
                }
                _curBranch++;
                if (_curBranch < _branches.Length)
                {
                    _probes[_curBranch].Bind(_seed);
                    _branches[_curBranch].Open(_tx!);
                    continue;
                }
                _hasSource = false;
            }

            if (!_source.MoveNext()) return false;
            _seed = _source.Current[_srcCol];
            _probes[0].Bind(_seed);
            _branches[0].Open(_tx!);
            _curBranch = 0;
            _hasSource = true;
            _branchEmitted = false;
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _branches.Length; i++) _branches[i].Dispose();
        _source.Dispose();
    }
}

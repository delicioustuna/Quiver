using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// Cypher の <c>UNION ALL</c> 相当。
/// 各入力行に対し、新しくバインドされた <see cref="CorrelatedInputOperator"/> を用いて
/// すべての分岐を再オープンし、各分岐の行を順番に放出する。
/// すべての分岐は単一列 NodeId のタプルを返す必要があり、出力スキーマも単一 NodeId 列となる。
/// </summary>
internal sealed class UnionOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly CorrelatedInputOperator[] _probes;
    private readonly IPhysicalOperator[] _branches;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("union", TupleSlotType.NodeId)]);

    private ITransaction? _tx;
    private int _curBranch = -1;
    private bool _hasSource;
    private TupleSlot _seed;

    public UnionOperator(
        IPhysicalOperator source,
        int sourceColumn,
        CorrelatedInputOperator[] probes,
        IPhysicalOperator[] branches)
    {
        if (probes is null || branches is null) throw new ArgumentNullException();
        if (probes.Length != branches.Length || branches.Length == 0)
            throw new ArgumentException("Union には少なくとも 1 組、かつ同数の (probe, branch) ペアが必要です。");
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
                    var s = Statistics; s.RowsProduced++; Statistics = s;
                    return true;
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
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _branches.Length; i++) _branches[i].Dispose();
        _source.Dispose();
    }
}

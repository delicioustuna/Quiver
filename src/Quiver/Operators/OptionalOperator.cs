using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// Cypher の <c>OPTIONAL MATCH</c> 相当。
/// 各入力行に対し、新しくバインドされた <see cref="CorrelatedInputOperator"/> を用いて
/// 分岐を評価する。分岐が少なくとも 1 行を生成すればそれを排出し、生成しなければ入力行の
/// エンティティ列を 1 回だけそのまま放出する — これにより左側の行がドロップされず、
/// 上流の値がそのまま流れる。
///
/// 分岐は単一列 VertexId のタプルを返す必要がある。フォールスルー時の形状はソースの
/// エンティティスロットの逐語コピーなので、出力列は常に単一 VertexId。
/// </summary>
internal sealed class OptionalOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly CorrelatedInputOperator _probe;
    private readonly IPhysicalOperator _branch;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("optional", TupleSlotType.VertexId)]);

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

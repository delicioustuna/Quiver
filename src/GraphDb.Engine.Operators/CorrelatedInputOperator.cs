using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

/// <summary>
/// Correlated subquery の起点となる単行演算子。
/// 外側タプルの値を Bind() でセットし、内側プランの Open() → MoveNext() で一度だけ emit する。
/// Open() を呼ぶたびに再アームされるため、SubquerySemiJoinPredicate が Open() を繰り返し呼ぶことで
/// 外側の各行に対して内側プランを再評価できる。
/// </summary>
public sealed class CorrelatedInputOperator : IPhysicalOperator
{
    private readonly TupleSlot[] _buffer = new TupleSlot[1];
    private TupleSlot _bound;
    private bool _pending;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("correlatedInput", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    /// <summary>外側タプルの該当スロットをバインドする。Open() の前に呼ぶこと。</summary>
    public void Bind(TupleSlot value) { _bound = value; }

    public void Open(ITransaction tx) { _pending = true; }

    public bool MoveNext()
    {
        if (!_pending) return false;
        _pending = false;
        _buffer[0] = _bound;
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() { }
}

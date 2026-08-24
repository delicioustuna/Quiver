using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// 相関サブクエリ述語。外側タプルの entityColumn 列を内側プランに渡し、
/// 内側プランが 1 行以上を返すかどうかで true/false を返す（EXISTS / NOT EXISTS）。
///
/// 内側プランは Evaluate() が呼ばれるたびに Open() でリセットされる。
/// CorrelatedInputOperator.Open() が re-arm するため、追加アロケーションは発生しない。
/// </summary>
internal sealed class SubquerySemiJoinPredicate : IPredicate, IDisposable
{
    private readonly int _outerEntityColumn;
    private readonly CorrelatedInputOperator _probe;
    private readonly IPhysicalOperator _innerPlan;
    private readonly bool _exists;
    private bool _opened;

    public SubquerySemiJoinPredicate(
        int outerEntityColumn,
        CorrelatedInputOperator probe,
        IPhysicalOperator innerPlan,
        bool exists = true)
    {
        _outerEntityColumn = outerEntityColumn;
        _probe = probe;
        _innerPlan = innerPlan;
        _exists = exists;
    }

    public bool Evaluate(in TupleRef outerTuple, ITransaction tx)
    {
        _probe.Bind(outerTuple[_outerEntityColumn]);
        _innerPlan.Open(tx);  // cascades to probe, re-arms it
        _opened = true;

        bool hasResult = _innerPlan.MoveNext();
        return _exists ? hasResult : !hasResult;
    }

    public void Dispose()
    {
        if (_opened) _innerPlan.Dispose();
    }
}

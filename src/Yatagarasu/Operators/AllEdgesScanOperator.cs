using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// 全Edgeを seq 順に放出するスキャンソース
/// (<c>g.Edges()</c> の起点)。列スキャン集約が適用できない場合の row path
/// フォールバックや <c>.ToList()</c> のために用いる。
/// </summary>
internal sealed class AllEdgesScanOperator : IPhysicalOperator
{
    private IEnumerator<EdgeId>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public TupleSchema Schema { get; } = new([new ColumnDefinition("edgeId", TupleSlotType.EdgeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _enumerator = tx.Edges.Scan().GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator!.MoveNext())
        {
            _buffer[0] = new TupleSlot { Type = TupleSlotType.EdgeId, LongValue = _enumerator.Current.Value };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose() => _enumerator?.Dispose();
}

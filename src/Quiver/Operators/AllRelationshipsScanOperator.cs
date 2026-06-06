using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// ARCH-5c Phase 5d: 全リレーションシップを seq 順に放出するスキャンソース
/// (<c>g.Relationships()</c> の起点)。列スキャン集約が適用できない場合の row path
/// フォールバックや <c>.ToList()</c> のために用いる。
/// </summary>
internal sealed class AllRelationshipsScanOperator : IPhysicalOperator
{
    private IEnumerator<RelationshipId>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public TupleSchema Schema { get; } = new([new ColumnDefinition("relId", TupleSlotType.RelationshipId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _enumerator = tx.Relationships.Scan().GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator!.MoveNext())
        {
            _buffer[0] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = _enumerator.Current.Value };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose() => _enumerator?.Dispose();
}

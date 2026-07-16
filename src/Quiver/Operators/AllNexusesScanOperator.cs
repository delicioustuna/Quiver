using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>可視な nexus header を sequence 順に放出するスキャンソース。</summary>
internal sealed class AllNexusesScanOperator : IPhysicalOperator
{
    private readonly NexusTypeId? _typeFilter;
    private ITransaction? _tx;
    private long _nextSequence;
    private long _highWaterMark;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public AllNexusesScanOperator(NexusTypeId? typeFilter = null)
        => _typeFilter = typeFilter;

    public TupleSchema Schema { get; } =
        new([new ColumnDefinition("nexusId", TupleSlotType.NexusId)]);

    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _nextSequence = 0;
        _highWaterMark = tx.Nexuses.SequenceHighWaterMark;
    }

    public bool MoveNext()
    {
        while (_nextSequence < _highWaterMark)
        {
            using var header = _tx!.Nexuses.Read(new NexusId(_nextSequence++));
            if (!header.InUse)
                continue;
            if (_typeFilter.HasValue && header.Type != _typeFilter.Value)
                continue;

            _buffer[0] = new TupleSlot
            {
                Type = TupleSlotType.NexusId,
                LongValue = header.Id.Value,
            };
            var statistics = Statistics;
            statistics.RowsProduced++;
            Statistics = statistics;
            return true;
        }
        return false;
    }

    public void Dispose() { }
}

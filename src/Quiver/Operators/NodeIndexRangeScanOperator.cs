using System.Text;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

internal sealed class NodeIndexRangeScanOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly ITupleProvider _fromProvider;
    private readonly bool _fromInclusive;
    private readonly ITupleProvider _toProvider;
    private readonly bool _toInclusive;
    private ITransaction? _tx;
    private IEnumerator<long>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public NodeIndexRangeScanOperator(
        string indexName,
        ITupleProvider fromProvider, bool fromInclusive,
        ITupleProvider toProvider, bool toInclusive)
    {
        _indexName = indexName;
        _fromProvider = fromProvider;
        _fromInclusive = fromInclusive;
        _toProvider = toProvider;
        _toInclusive = toInclusive;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        var emptyRef = new TupleRef(Span<TupleSlot>.Empty);
        IEnumerable<long> nodeIds;

        switch (_fromProvider.SlotType)
        {
            case TupleSlotType.Int64:
            case TupleSlotType.NodeId:
            {
                long from = _fromProvider.Provide(in emptyRef, tx).LongValue;
                long to = _toProvider.Provide(in emptyRef, tx).LongValue;
                nodeIds = tx.Indexes.CreateInt64Index(_indexName)
                    .RangeValues(from, _fromInclusive, to, _toInclusive);
                break;
            }
            case TupleSlotType.Double:
            {
                double from = _fromProvider.Provide(in emptyRef, tx).DoubleValue;
                double to = _toProvider.Provide(in emptyRef, tx).DoubleValue;
                nodeIds = tx.Indexes.CreateDoubleIndex(_indexName)
                    .RangeValues(from, _fromInclusive, to, _toInclusive);
                break;
            }
            case TupleSlotType.Utf8String:
            {
                var fromBytes = _fromProvider.ProvideBytes(in emptyRef, tx);
                var toBytes = _toProvider.ProvideBytes(in emptyRef, tx);
                string from = Encoding.UTF8.GetString(fromBytes);
                string to = Encoding.UTF8.GetString(toBytes);
                nodeIds = tx.Indexes.CreateStringIndex(_indexName)
                    .RangeValues(from, _fromInclusive, to, _toInclusive);
                break;
            }
            default:
                nodeIds = [];
                break;
        }
        _enumerator = nodeIds.GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator == null || !_enumerator.MoveNext()) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _enumerator.Current };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() { _enumerator?.Dispose(); }
}

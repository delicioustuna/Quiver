using System.Text;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class NodeIndexSeekOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly ITupleProvider _keyProvider;
    private ITransaction? _tx;
    private IEnumerator<long>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public NodeIndexSeekOperator(string indexName, ITupleProvider keyProvider)
    {
        _indexName = indexName;
        _keyProvider = keyProvider;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        var emptyRef = new TupleRef(Span<TupleSlot>.Empty);
        IEnumerable<long> nodeIds;

        switch (_keyProvider.SlotType)
        {
            case TupleSlotType.Int64:
            case TupleSlotType.NodeId:
            case TupleSlotType.RelationshipId:
            {
                long key = _keyProvider.Provide(in emptyRef, tx).LongValue;
                nodeIds = tx.Indexes.CreateInt64Index(_indexName).SeekValues(key);
                break;
            }
            case TupleSlotType.Double:
            {
                double key = _keyProvider.Provide(in emptyRef, tx).DoubleValue;
                nodeIds = tx.Indexes.CreateDoubleIndex(_indexName).SeekValues(key);
                break;
            }
            case TupleSlotType.Utf8String:
            {
                var bytes = _keyProvider.ProvideBytes(in emptyRef, tx);
                string key = Encoding.UTF8.GetString(bytes);
                nodeIds = tx.Indexes.CreateStringIndex(_indexName).SeekValues(key);
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

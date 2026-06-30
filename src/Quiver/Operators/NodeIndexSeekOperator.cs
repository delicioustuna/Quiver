using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// B-tree インデックスに対して等値検索を行い、合致するノードを列挙するオペレータ。
/// </summary>
internal sealed class NodeIndexSeekOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly ITupleProvider _keyProvider;
    private IEnumerator<NodeId>? _enumerator;
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
        var emptyRef = new TupleRef(Span<TupleSlot>.Empty);
        var key = _keyProvider.SlotType switch
        {
            TupleSlotType.Int64 or TupleSlotType.NodeId or TupleSlotType.RelationshipId =>
                PropertyValue.FromInt64(_keyProvider.Provide(in emptyRef, tx).LongValue),
            TupleSlotType.Double =>
                PropertyValue.FromDouble(_keyProvider.Provide(in emptyRef, tx).DoubleValue),
            TupleSlotType.Utf8String =>
                PropertyValue.FromUtf8(_keyProvider.ProvideBytes(in emptyRef, tx)),
            _ => default,
        };
        _enumerator = tx.Access.SeekNodesByIndex(tx, _indexName, key).GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator == null || !_enumerator.MoveNext()) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _enumerator.Current.Value };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() { _enumerator?.Dispose(); }
}

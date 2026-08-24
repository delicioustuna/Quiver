using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// B-tree インデックスに対して等値検索を行い、合致するVertexを列挙するオペレータ。
/// </summary>
internal sealed class VertexIndexSeekOperator : IPhysicalOperator
{
    private readonly ScalarIndexDefinition _definition;
    private readonly PropertyKeyId _propertyKey;
    private readonly LabelId? _scopeLabel;
    private readonly ITupleProvider _keyProvider;
    private IEnumerator<VertexId>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public VertexIndexSeekOperator(
        ScalarIndexDefinition definition,
        PropertyKeyId propertyKey,
        LabelId? scopeLabel,
        ITupleProvider keyProvider)
    {
        _definition = definition;
        _propertyKey = propertyKey;
        _scopeLabel = scopeLabel;
        _keyProvider = keyProvider;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        var emptyRef = new TupleRef(Span<TupleSlot>.Empty);
        var key = _keyProvider.SlotType switch
        {
            TupleSlotType.Int64 or TupleSlotType.VertexId or TupleSlotType.EdgeId =>
                PropertyValue.FromInt64(_keyProvider.Provide(in emptyRef, tx).LongValue),
            TupleSlotType.Double =>
                PropertyValue.FromDouble(_keyProvider.Provide(in emptyRef, tx).DoubleValue),
            TupleSlotType.Utf8String =>
                PropertyValue.FromUtf8(_keyProvider.ProvideBytes(in emptyRef, tx)),
            _ => default,
        };
        _enumerator = tx.Access.SeekVerticesByIndex(
            tx,
            _definition,
            _propertyKey,
            _scopeLabel,
            key).GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator == null || !_enumerator.MoveNext()) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _enumerator.Current.Value };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() { _enumerator?.Dispose(); }
}

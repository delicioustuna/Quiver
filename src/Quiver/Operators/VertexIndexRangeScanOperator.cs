using System.Text;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// B-tree インデックスに対して範囲スキャンを行い、合致するVertexを列挙するオペレータ。
/// Int64 / Double / UTF-8 String の各型を <see cref="ITupleProvider"/> から取得して
/// 型に応じた RangeValues を呼び出す。
/// </summary>
internal sealed class VertexIndexRangeScanOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly ITupleProvider _fromProvider;
    private readonly bool _fromInclusive;
    private readonly ITupleProvider _toProvider;
    private readonly bool _toInclusive;
    private ITransaction? _tx;
    private IEnumerator<long>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public VertexIndexRangeScanOperator(
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

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        var emptyRef = new TupleRef(Span<TupleSlot>.Empty);
        IEnumerable<long> vertexIds;

        switch (_fromProvider.SlotType)
        {
            case TupleSlotType.Int64:
            case TupleSlotType.VertexId:
            {
                long from = _fromProvider.Provide(in emptyRef, tx).LongValue;
                long to = _toProvider.Provide(in emptyRef, tx).LongValue;
                vertexIds = tx.Indexes.CreateInt64Index(_indexName)
                    .RangeValues(from, _fromInclusive, to, _toInclusive);
                break;
            }
            case TupleSlotType.Double:
            {
                double from = _fromProvider.Provide(in emptyRef, tx).DoubleValue;
                double to = _toProvider.Provide(in emptyRef, tx).DoubleValue;
                vertexIds = tx.Indexes.CreateDoubleIndex(_indexName)
                    .RangeValues(from, _fromInclusive, to, _toInclusive);
                break;
            }
            case TupleSlotType.Utf8String:
            {
                var fromBytes = _fromProvider.ProvideBytes(in emptyRef, tx);
                var toBytes = _toProvider.ProvideBytes(in emptyRef, tx);
                string from = Encoding.UTF8.GetString(fromBytes);
                string to = Encoding.UTF8.GetString(toBytes);
                vertexIds = tx.Indexes.CreateStringIndex(_indexName)
                    .RangeValues(from, _fromInclusive, to, _toInclusive);
                break;
            }
            default:
                vertexIds = [];
                break;
        }
        // 索引値はパック済み (Kind/Generation/Sequence)。世代照合しつつ
        // VertexId.Value へ unpack し、slot 再利用 (ABA) の stale 参照を弾く。
        _enumerator = IndexValueResolver.ResolveLiveVertexSequences(vertexIds, tx.Vertices).GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator == null || !_enumerator.MoveNext()) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _enumerator.Current };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() { _enumerator?.Dispose(); }
}

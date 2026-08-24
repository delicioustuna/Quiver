using System.Text;
using Yatagarasu.Core;
using Yatagarasu.Index;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// B-tree インデックスに対して範囲スキャンを行い、合致するVertexを列挙するオペレータ。
/// Int64 / Double / UTF-8 String の各型を <see cref="ITupleProvider"/> から取得して
/// 型に応じた RangeValues を呼び出す。
/// </summary>
internal sealed class VertexIndexRangeScanOperator : IPhysicalOperator
{
    private readonly ScalarIndexDefinition _definition;
    private readonly PropertyKeyId _propertyKey;
    private readonly LabelId? _scopeLabel;
    private readonly ITupleProvider _fromProvider;
    private readonly bool _fromInclusive;
    private readonly ITupleProvider _toProvider;
    private readonly bool _toInclusive;
    private ITransaction? _tx;
    private IEnumerator<long>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public VertexIndexRangeScanOperator(
        ScalarIndexDefinition definition,
        PropertyKeyId propertyKey,
        LabelId? scopeLabel,
        ITupleProvider fromProvider, bool fromInclusive,
        ITupleProvider toProvider, bool toInclusive)
    {
        _definition = definition;
        _propertyKey = propertyKey;
        _scopeLabel = scopeLabel;
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
        ScalarRangeFilter filter;
        ScalarIndexMetadata metadata = tx.Indexes.ListIndexDefinitions()
            .FirstOrDefault(candidate =>
                candidate.Definition.Name == _definition.Name);
        bool useIndex = metadata.Definition is not null
            && metadata.State == IndexLifecycleState.Ready;

        switch (_fromProvider.SlotType)
        {
            case TupleSlotType.Int64:
            case TupleSlotType.VertexId:
            {
                long from = _fromProvider.Provide(in emptyRef, tx).LongValue;
                long to = _toProvider.Provide(in emptyRef, tx).LongValue;
                filter = new ScalarRangeFilter(
                    _definition.Kind,
                    from,
                    to,
                    0,
                    0,
                    null,
                    null,
                    _fromInclusive,
                    _toInclusive);
                vertexIds = !useIndex
                    ? []
                    : _definition.Kind == IndexKind.Int32Equality
                        ? tx.Indexes.CreateInt32Index(_definition.Name)
                            .RangeValues(
                                checked((int)from),
                                _fromInclusive,
                                checked((int)to),
                                _toInclusive)
                        : tx.Indexes.CreateInt64Index(_definition.Name)
                            .RangeValues(from, _fromInclusive, to, _toInclusive);
                break;
            }
            case TupleSlotType.Double:
            {
                double from = _fromProvider.Provide(in emptyRef, tx).DoubleValue;
                double to = _toProvider.Provide(in emptyRef, tx).DoubleValue;
                filter = new ScalarRangeFilter(
                    _definition.Kind,
                    0,
                    0,
                    from,
                    to,
                    null,
                    null,
                    _fromInclusive,
                    _toInclusive);
                vertexIds = useIndex
                    ? tx.Indexes.CreateDoubleIndex(_definition.Name)
                        .RangeValues(from, _fromInclusive, to, _toInclusive)
                    : [];
                break;
            }
            case TupleSlotType.Utf8String:
            {
                var fromBytes = _fromProvider.ProvideBytes(in emptyRef, tx);
                var toBytes = _toProvider.ProvideBytes(in emptyRef, tx);
                string from = Encoding.UTF8.GetString(fromBytes);
                string to = Encoding.UTF8.GetString(toBytes);
                filter = new ScalarRangeFilter(
                    _definition.Kind,
                    0,
                    0,
                    0,
                    0,
                    from,
                    to,
                    _fromInclusive,
                    _toInclusive);
                vertexIds = useIndex
                    ? tx.Indexes.CreateStringIndex(_definition.Name)
                        .RangeValues(from, _fromInclusive, to, _toInclusive)
                    : [];
                break;
            }
            default:
                vertexIds = [];
                filter = default;
                break;
        }

        if (!useIndex && metadata.Definition is not null)
            vertexIds = ScanPrimary(tx, filter);

        _enumerator = IndexValueResolver
            .ResolveVisibleVertexPropertyOwners(
                vertexIds,
                tx,
                _propertyKey,
                _scopeLabel,
                filter)
            .GetEnumerator();
    }

    private IEnumerable<long> ScanPrimary(
        ITransaction transaction,
        ScalarRangeFilter filter)
    {
        var versions = new List<(RangeSortKey Key, long Version)>();
        foreach (VertexId vertexId in transaction.Vertices.Scan())
        {
            VertexReadHandle vertex = transaction.Vertices.Read(vertexId);
            if (!vertex.InUse
                || _scopeLabel is { } scope && vertex.Label != scope)
                continue;

            PropertyCursor properties = transaction.Vertices.EnumerateProperties(
                vertexId,
                transaction.Properties);
            while (properties.MoveNext())
            {
                PropertyEntry property = properties.Current;
                PropertyValue value = property.Value;
                if (property.KeyId != _propertyKey
                    || !filter.Matches(in value)
                    || !RangeSortKey.TryCreate(
                        _definition.Kind,
                        in value,
                        out RangeSortKey key))
                    continue;
                versions.Add((key, properties.CurrentVersion.Value));
            }
        }

        versions.Sort(static (left, right) =>
        {
            int key = left.Key.CompareTo(right.Key);
            return key != 0
                ? key
                : left.Version.CompareTo(right.Version);
        });
        return versions.Select(static item => item.Version);
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

    private readonly record struct RangeSortKey(
        IndexKind Kind,
        long Scalar,
        double FloatingPoint,
        string? Text) : IComparable<RangeSortKey>
    {
        internal static bool TryCreate(
            IndexKind kind,
            in PropertyValue value,
            out RangeSortKey key)
        {
            key = kind switch
            {
                IndexKind.Int32Equality when value.Type == PropertyValueType.Int32
                    => new(kind, value.Int32Value, 0, null),
                IndexKind.Int64Equality when value.Type is
                    PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64
                    => new(kind, value.Int64Value, 0, null),
                IndexKind.DoubleEquality when value.Type == PropertyValueType.Double
                    => new(kind, 0, value.DoubleValue, null),
                IndexKind.StringEquality or IndexKind.StringRange
                    when value.Type == PropertyValueType.String
                    => new(
                        kind,
                        0,
                        0,
                        Encoding.UTF8.GetString(value.Utf8StringValue)),
                _ => default,
            };
            return kind switch
            {
                IndexKind.Int32Equality => value.Type == PropertyValueType.Int32,
                IndexKind.Int64Equality => value.Type is
                    PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64,
                IndexKind.DoubleEquality => value.Type == PropertyValueType.Double,
                IndexKind.StringEquality or IndexKind.StringRange
                    => value.Type == PropertyValueType.String,
                _ => false,
            };
        }

        public int CompareTo(RangeSortKey other)
            => Kind switch
            {
                IndexKind.Int32Equality or IndexKind.Int64Equality
                    => Scalar.CompareTo(other.Scalar),
                IndexKind.DoubleEquality
                    => FloatingPoint.CompareTo(other.FloatingPoint),
                IndexKind.StringEquality or IndexKind.StringRange
                    => string.Compare(Text, other.Text, StringComparison.Ordinal),
                _ => 0,
            };
    }
}

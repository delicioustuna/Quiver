using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// 上流のエンティティ ID 列からプロパティ値を読み取り、末尾に 1 列付加するオペレータ。
/// VertexとEdgeの両方に対応する。
/// </summary>
internal sealed class PropertyLookupOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _entityIdColumn;
    private readonly PropertyKeyId _keyId;
    private readonly string _outputColumnName;
    private readonly PropertyTypeFlags _expectedTypes;
    // entity 列が Vertex か Edge か。Edge のときは edge ストアの
    // 結合プロパティ列挙子を使う (g.Edges() の row path フォールバック用)。
    private readonly EntityKind _entityKind;
    private ITransaction? _tx;
    private TupleSlot[]? _buffer;
    private TupleSchema? _schema;
    private byte[]? _currentBytes;

    public PropertyLookupOperator(
        IPhysicalOperator source,
        int entityIdColumn,
        PropertyKeyId keyId,
        string outputColumnName)
        : this(source, entityIdColumn, keyId, outputColumnName, PropertyTypeFlags.Scalar, EntityKind.Vertex)
    {
    }

    /// <summary>
    /// 期待する型を指定する overload。<paramref name="expectedTypes"/> が
    /// <see cref="PropertyTypeFlags.Scalar"/> より狭い場合、対象外の型は
    /// string / bytes ペイロードを実体化せずスキップする。
    /// </summary>
    public PropertyLookupOperator(
        IPhysicalOperator source,
        int entityIdColumn,
        PropertyKeyId keyId,
        string outputColumnName,
        PropertyTypeFlags expectedTypes)
        : this(source, entityIdColumn, keyId, outputColumnName, expectedTypes, EntityKind.Vertex)
    {
    }

    /// <summary>entity kind を指定する overload (Edge のとき edge プロパティを読む)。</summary>
    public PropertyLookupOperator(
        IPhysicalOperator source,
        int entityIdColumn,
        PropertyKeyId keyId,
        string outputColumnName,
        PropertyTypeFlags expectedTypes,
        EntityKind entityKind)
    {
        _source = source;
        _entityIdColumn = entityIdColumn;
        _keyId = keyId;
        _outputColumnName = outputColumnName;
        _expectedTypes = expectedTypes == PropertyTypeFlags.None
            ? PropertyTypeFlags.Scalar
            : expectedTypes;
        _entityKind = entityKind;
    }

    public TupleSchema Schema => _schema ?? new TupleSchema([]);

    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer!);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        var cols = new List<ColumnDefinition>(_source.Schema.Columns)
        {
            new ColumnDefinition(_outputColumnName, TupleSlotType.Null)
        };
        _schema = new TupleSchema(cols);
        _buffer = new TupleSlot[cols.Count];
    }

    public bool MoveNext()
    {
        if (!_source.MoveNext()) return false;

        var cur = _source.Current;
        int srcCols = _source.Schema.Columns.Count;

        // ソーススロットをコピー
        for (int i = 0; i < srcCols; i++)
            _buffer![i] = cur[i];

        // プロパティ値を検索
        _currentBytes = null;
        _buffer![srcCols] = default; // Null by default

        // inline + overflow チェーンを結合して走査する。entity kind により
        // vertex / edge / nexus のプロパティストアを切り替える。
        long localId = cur[_entityIdColumn].LongValue;
        var propEnum = _entityKind switch
        {
            EntityKind.Edge =>
                _tx!.Edges.EnumerateProperties(new EdgeId(localId), _tx.Properties),
            EntityKind.Nexus =>
                _tx!.Nexuses.EnumerateProperties(new NexusId(localId), _tx.Properties),
            _ => _tx!.Vertices.EnumerateProperties(new VertexId(localId), _tx.Properties),
        };
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId != _keyId) continue;

            var val = prop.Value;
            // 期待する型集合に含まれない値は string / bytes コピー前に拒否する。出力は Null のまま。
            if ((val.Type.ToFlags() & _expectedTypes) == PropertyTypeFlags.None)
                break;

            if (val.Type == PropertyValueType.String)
            {
                _currentBytes = val.Utf8StringValue.ToArray();
                _buffer![srcCols] = new TupleSlot
                    { Type = TupleSlotType.Utf8String, BytesOffset = 0, BytesLength = _currentBytes.Length };
            }
            else if (val.Type == PropertyValueType.Bytes)
            {
                _currentBytes = val.BytesValue.ToArray();
                _buffer![srcCols] = new TupleSlot
                    { Type = TupleSlotType.Bytes, BytesOffset = 0, BytesLength = _currentBytes.Length };
            }
            else if (val.Type == PropertyValueType.FloatArray)
            {
                _currentBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(val.FloatArrayValue).ToArray();
                _buffer![srcCols] = new TupleSlot
                    { Type = TupleSlotType.Bytes, BytesOffset = 0, BytesLength = _currentBytes.Length };
            }
            else
            {
                _buffer![srcCols] = PropertyValueToSlot(val);
            }
            break;
        }

        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public ReadOnlySpan<byte> GetBytes(int column)
    {
        int propCol = _source.Schema.Columns.Count;
        if (column == propCol) return _currentBytes ?? ReadOnlySpan<byte>.Empty;
        return _source.GetBytes(column);
    }

    private static TupleSlot PropertyValueToSlot(PropertyValue val) => val.Type switch
    {
        PropertyValueType.Bool  => new TupleSlot { Type = TupleSlotType.Bool,  LongValue = val.BoolValue ? 1L : 0L },
        PropertyValueType.Int32 => new TupleSlot { Type = TupleSlotType.Int64, LongValue = val.Int32Value },
        PropertyValueType.Int64 => new TupleSlot { Type = TupleSlotType.Int64, LongValue = val.Int64Value },
        PropertyValueType.Double => new TupleSlot { Type = TupleSlotType.Double, DoubleValue = val.DoubleValue },
        _ => default,
    };

    public void Dispose() { _source.Dispose(); }
}

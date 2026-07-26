using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// ソースVertexの隣接エッジを走査し、出力モードに応じて近傍Vertex・Edge・
/// 重みを放出する 1 ホップ展開演算子。
/// </summary>
internal sealed class ExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceVertexColumn;
    private readonly Direction _direction;
    private readonly EdgeTypeId? _typeFilter;
    private readonly ExpandOutputMode _outputMode;
    // non-null のとき、上流の列値を出力タプルの末尾にコピーする。
    // インデックスセットは毎回同一なのでコンストラクタで 1 度だけ確保する。
    private readonly int[]? _carryColumns;
    private readonly int _baseColumnCount;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;
    private TupleSlotType _weightSlotType = TupleSlotType.Int64;

    private ExpandCursor? _cursor;
    private VertexId _currentSourceVertex;

    public ExpandOperator(
        IPhysicalOperator source,
        int sourceVertexColumn,
        Direction direction,
        EdgeTypeId? typeFilter,
        ExpandOutputMode outputMode,
        int[]? carryColumns = null)
    {
        _source = source;
        _sourceVertexColumn = sourceVertexColumn;
        _direction = direction;
        _typeFilter = typeFilter;
        _outputMode = outputMode;
        _carryColumns = (carryColumns is { Length: > 0 }) ? carryColumns : null;

        IReadOnlyList<ColumnDefinition> baseCols = outputMode switch
        {
            ExpandOutputMode.NeighborOnly => new ColumnDefinition[]
            {
                new("neighbor", TupleSlotType.VertexId),
            },
            ExpandOutputMode.NeighborAndEdge => new ColumnDefinition[]
            {
                new("edge",      TupleSlotType.EdgeId),
                new("neighbor", TupleSlotType.VertexId),
            },
            ExpandOutputMode.NeighborAndWeight => new ColumnDefinition[]
            {
                new("edge",      TupleSlotType.EdgeId),
                new("neighbor", TupleSlotType.VertexId),
                new("weight",   TupleSlotType.Int64),
            },
            _ /* Full */ => new ColumnDefinition[]
            {
                new("source",   TupleSlotType.VertexId),
                new("edge",      TupleSlotType.EdgeId),
                new("neighbor", TupleSlotType.VertexId),
            },
        };

        _baseColumnCount = baseCols.Count;
        int carryCount = _carryColumns?.Length ?? 0;
        _buffer = new TupleSlot[_baseColumnCount + carryCount];

        if (_carryColumns == null)
        {
            _schema = new TupleSchema(baseCols);
        }
        else
        {
            var cols = new List<ColumnDefinition>(_baseColumnCount + carryCount);
            cols.AddRange(baseCols);
            var srcSchema = source.Schema;
            for (int i = 0; i < _carryColumns.Length; i++)
            {
                var srcDef = srcSchema.Columns[_carryColumns[i]];
                cols.Add(new ColumnDefinition(srcDef.Name, srcDef.Type));
            }
            _schema = new TupleSchema(cols);
        }

        _currentSourceVertex = VertexId.Invalid;
    }

    public TupleSchema Schema => _schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _currentSourceVertex = VertexId.Invalid;
        _cursor = null;

        // 重み出力時、スロット型を payload lane の Kind に合わせる。
        // payload lane が無い場合はスロットは Int64 のままゼロを保持する
        // (呼び出し元はデータではなくスキーマで分岐できる)。
        if (_outputMode == ExpandOutputMode.NeighborAndWeight
            && tx.AdjacencySegments is IAdjacencyPayloadView pl
            && pl.PayloadSpec.Kind == PayloadKind.Double)
        {
            _weightSlotType = TupleSlotType.Double;
        }
        else
        {
            _weightSlotType = TupleSlotType.Int64;
        }
    }

    public bool MoveNext()
    {
        while (true)
        {
            if (_cursor != null && _cursor.MoveNext())
            {
                BuildOutput(_cursor.Neighbor, _cursor.Edge, _cursor.WeightRaw);
                var s = Statistics;
                s.RowsProduced++;
                Statistics = s;
                return true;
            }

            _cursor?.Dispose();
            _cursor = null;

            if (!_source.MoveNext()) return false;
            _currentSourceVertex = new VertexId(_source.Current[_sourceVertexColumn].LongValue);
            _cursor = _tx!.Access.Expand(_tx, _currentSourceVertex, _direction, _typeFilter);

            // ソースVertexに隣接ブロックがある場合のみヒットとして計上する。
            // AdjacencyFallbackCount (ブロック不在時のみ発火) とは異なり、
            // オプティマイザが ExpandStrategy 選択時に EdgeScanRecords と
            // 比較するための per-operator ヒット数を提供する。
            if (_tx!.AdjacencySegments?.HasBlock(_currentSourceVertex) == true)
            {
                var s = Statistics;
                s.AdjacencyBlockHits++;
                Statistics = s;
            }
        }
    }

    private void BuildOutput(VertexId neighbor, EdgeId edgeId, long weightRaw)
    {
        switch (_outputMode)
        {
            case ExpandOutputMode.NeighborOnly:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = neighbor.Value };
                break;
            case ExpandOutputMode.NeighborAndEdge:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.EdgeId, LongValue = edgeId.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = neighbor.Value };
                break;
            case ExpandOutputMode.NeighborAndWeight:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.EdgeId, LongValue = edgeId.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = neighbor.Value };
                _buffer[2] = new TupleSlot { Type = _weightSlotType, LongValue = weightRaw };
                break;
            default:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _currentSourceVertex.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.EdgeId, LongValue = edgeId.Value };
                _buffer[2] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = neighbor.Value };
                break;
        }

        // carry 対象の上流列をタプル末尾にコピーする。_source.Current は
        // _cursor を生成した行をまだ指しているのでスロットは有効。
        if (_carryColumns != null)
        {
            var src = _source.Current;
            for (int i = 0; i < _carryColumns.Length; i++)
                _buffer[_baseColumnCount + i] = src[_carryColumns[i]];
        }
    }

    // carry 列のバイトペイロード (Utf8String / Bytes) はローカルにスナップショット
    // しないため、上流オペレータから取得する必要がある。
    public ReadOnlySpan<byte> GetBytes(int column)
    {
        if (_carryColumns != null && column >= _baseColumnCount)
        {
            int carryIdx = column - _baseColumnCount;
            return _source.GetBytes(_carryColumns[carryIdx]);
        }
        return ReadOnlySpan<byte>.Empty;
    }

    public void Dispose()
    {
        _cursor?.Dispose();
        _cursor = null;
        _source.Dispose();
    }
}

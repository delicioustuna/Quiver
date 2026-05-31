using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Operators;

public sealed class ExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly ExpandOutputMode _outputMode;
    // GC-6: when non-null, copy these upstream column values into the tail of
    // the output tuple per emitted edge. Same set of indices each call —
    // allocated once in ctor.
    private readonly int[]? _carryColumns;
    private readonly int _baseColumnCount;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;
    private TupleSlotType _weightSlotType = TupleSlotType.Int64;

    private ExpandCursor? _cursor;
    private NodeId _currentSourceNode;

    public ExpandOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        ExpandOutputMode outputMode,
        int[]? carryColumns = null)
    {
        _source = source;
        _sourceNodeColumn = sourceNodeColumn;
        _direction = direction;
        _typeFilter = typeFilter;
        _outputMode = outputMode;
        _carryColumns = (carryColumns is { Length: > 0 }) ? carryColumns : null;

        IReadOnlyList<ColumnDefinition> baseCols = outputMode switch
        {
            ExpandOutputMode.NeighborOnly => new ColumnDefinition[]
            {
                new("neighbor", TupleSlotType.NodeId),
            },
            ExpandOutputMode.NeighborAndRel => new ColumnDefinition[]
            {
                new("rel",      TupleSlotType.RelationshipId),
                new("neighbor", TupleSlotType.NodeId),
            },
            ExpandOutputMode.NeighborAndWeight => new ColumnDefinition[]
            {
                new("rel",      TupleSlotType.RelationshipId),
                new("neighbor", TupleSlotType.NodeId),
                new("weight",   TupleSlotType.Int64),
            },
            _ /* Full */ => new ColumnDefinition[]
            {
                new("source",   TupleSlotType.NodeId),
                new("rel",      TupleSlotType.RelationshipId),
                new("neighbor", TupleSlotType.NodeId),
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

        _currentSourceNode = NodeId.Invalid;
    }

    public TupleSchema Schema => _schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _currentSourceNode = NodeId.Invalid;
        _cursor = null;

        // BA-6: when emitting weights, type the slot to match the V2 payload
        // lane's kind. If the underlying store has no payload lane the slot
        // stays Int64 and will carry zero — the operator contract documents
        // this fallback so callers can branch on schema rather than data.
        if (_outputMode == ExpandOutputMode.NeighborAndWeight
            && tx.AdjacencyBlocks is IAdjacencyPayloadView pl
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
                BuildOutput(_cursor.Neighbor, _cursor.Relationship, _cursor.WeightRaw);
                var s = Statistics;
                s.RowsProduced++;
                Statistics = s;
                return true;
            }

            _cursor?.Dispose();
            _cursor = null;

            if (!_source.MoveNext()) return false;
            _currentSourceNode = new NodeId(_source.Current[_sourceNodeColumn].LongValue);
            _cursor = _tx!.Access.Expand(_tx, _currentSourceNode, _direction, _typeFilter);

            // PW-17 attribution: count this expansion as an adjacency-block hit
            // when the source node actually has a block. Diverging from
            // AdjacencyFallbackCount (which only fires on "no block at all"),
            // this gives the optimizer a per-operator hit count to compare
            // against RelationshipScanRecords when picking ExpandStrategy.
            if (_tx!.AdjacencyBlocks?.HasBlock(_currentSourceNode) == true)
            {
                var s = Statistics;
                s.AdjacencyBlockHits++;
                Statistics = s;
            }
        }
    }

    private void BuildOutput(NodeId neighbor, RelationshipId relId, long weightRaw)
    {
        switch (_outputMode)
        {
            case ExpandOutputMode.NeighborOnly:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
            case ExpandOutputMode.NeighborAndRel:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = relId.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
            case ExpandOutputMode.NeighborAndWeight:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = relId.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                _buffer[2] = new TupleSlot { Type = _weightSlotType, LongValue = weightRaw };
                break;
            default:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _currentSourceNode.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = relId.Value };
                _buffer[2] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
        }

        // GC-6: copy carried upstream columns into the tail. _source.Current is
        // still pointing at the row that produced _cursor, so the slots remain
        // valid here (byte data is also accessible via _source.GetBytes).
        if (_carryColumns != null)
        {
            var src = _source.Current;
            for (int i = 0; i < _carryColumns.Length; i++)
                _buffer[_baseColumnCount + i] = src[_carryColumns[i]];
        }
    }

    // GC-6: byte payload (Utf8String / Bytes) for carry columns has to come
    // from the upstream operator because we don't snapshot it locally.
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

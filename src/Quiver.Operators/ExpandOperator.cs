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
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;

    private ExpandCursor? _cursor;
    private NodeId _currentSourceNode;

    public ExpandOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        ExpandOutputMode outputMode)
    {
        _source = source;
        _sourceNodeColumn = sourceNodeColumn;
        _direction = direction;
        _typeFilter = typeFilter;
        _outputMode = outputMode;
        (_buffer, _schema) = outputMode switch
        {
            ExpandOutputMode.NeighborOnly => (new TupleSlot[1], new TupleSchema([
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
            ExpandOutputMode.NeighborAndRel => (new TupleSlot[2], new TupleSchema([
                new ColumnDefinition("rel", TupleSlotType.RelationshipId),
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
            _ => (new TupleSlot[3], new TupleSchema([
                new ColumnDefinition("source", TupleSlotType.NodeId),
                new ColumnDefinition("rel", TupleSlotType.RelationshipId),
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
        };
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
    }

    public bool MoveNext()
    {
        while (true)
        {
            if (_cursor != null && _cursor.MoveNext())
            {
                BuildOutput(_cursor.Neighbor, _cursor.Relationship);
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

    private void BuildOutput(NodeId neighbor, RelationshipId relId)
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
            default:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _currentSourceNode.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = relId.Value };
                _buffer[2] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
        }
    }

    public void Dispose()
    {
        _cursor?.Dispose();
        _cursor = null;
        _source.Dispose();
    }
}

using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class ExpandOperator : IPhysicalOperator
{
    // When the adjacency buffer fills exactly, we cannot tell whether the node's degree
    // equals the buffer size or exceeds it. Fall back to linked list in that case.
    private const int AdjBufferSize = 8192;

    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly ExpandOutputMode _outputMode;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;

    // Adjacency block fast path
    private IAdjacencyBlockStore? _adjStore;
    private readonly AdjacencyEntry[] _adjBuffer = new AdjacencyEntry[AdjBufferSize];
    private int _adjCount;
    private int _adjIdx;
    private bool _usingAdj;

    // Manual relationship chain tracking (linked-list fallback)
    private NodeId _currentSourceNode;
    private RelationshipId _nextRelId;

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
        _nextRelId = RelationshipId.Invalid;
    }

    public TupleSchema Schema => _schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _adjStore = tx.AdjacencyBlocks;
        _source.Open(tx);
        _currentSourceNode = NodeId.Invalid;
        _nextRelId = RelationshipId.Invalid;
        _adjCount = 0;
        _adjIdx = 0;
        _usingAdj = false;
    }

    public bool MoveNext()
    {
        while (true)
        {
            if (_usingAdj)
            {
                // Fast path: iterate contiguous adjacency buffer.
                while (_adjIdx < _adjCount)
                {
                    var entry = _adjBuffer[_adjIdx++];
                    BuildOutputFromEntry(entry);
                    var s = Statistics;
                    s.RowsProduced++;
                    Statistics = s;
                    return true;
                }
                // Adjacency buffer exhausted for this source node.
            }
            else
            {
                // Linked-list fallback: walk the relationship chain.
                while (_nextRelId.IsValid)
                {
                    var rel = _tx!.Relationships.Read(_nextRelId);

                    RelationshipId next = rel.Source == _currentSourceNode
                        ? rel.SourceNext
                        : rel.TargetNext;
                    _nextRelId = next;

                    bool typeOk = !_typeFilter.HasValue || rel.Type == _typeFilter.Value;
                    bool dirOk = _direction switch
                    {
                        Direction.Outgoing => rel.Source == _currentSourceNode,
                        Direction.Incoming => rel.Target == _currentSourceNode,
                        _ => true,
                    };

                    if (typeOk && dirOk)
                    {
                        BuildOutput(rel);
                        var s = Statistics;
                        s.RowsProduced++;
                        Statistics = s;
                        return true;
                    }
                }
            }

            // Advance to the next source node.
            if (!_source.MoveNext()) return false;
            _currentSourceNode = new NodeId(_source.Current[_sourceNodeColumn].LongValue);
            LoadNeighbors(_currentSourceNode);
        }
    }

    private void LoadNeighbors(NodeId nodeId)
    {
        if (_adjStore != null && _adjStore.HasBlock(nodeId))
        {
            int count = _adjStore.ReadEdges(nodeId, _direction, _typeFilter, _adjBuffer);
            // If buffer filled exactly, degree may exceed it — fall back to linked list.
            if (count < AdjBufferSize)
            {
                _adjCount = count;
                _adjIdx = 0;
                _usingAdj = true;
                return;
            }
        }
        _usingAdj = false;
        _nextRelId = _tx!.Nodes.Read(nodeId).FirstRelationshipId;
    }

    private void BuildOutputFromEntry(AdjacencyEntry entry)
    {
        switch (_outputMode)
        {
            case ExpandOutputMode.NeighborOnly:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = entry.NeighborId.Value };
                break;
            case ExpandOutputMode.NeighborAndRel:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = entry.RelId.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = entry.NeighborId.Value };
                break;
            default:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _currentSourceNode.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = entry.RelId.Value };
                _buffer[2] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = entry.NeighborId.Value };
                break;
        }
    }

    private void BuildOutput(RelationshipReadHandle rel)
    {
        NodeId neighbor = rel.Source == _currentSourceNode ? rel.Target : rel.Source;
        switch (_outputMode)
        {
            case ExpandOutputMode.NeighborOnly:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
            case ExpandOutputMode.NeighborAndRel:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = rel.Id.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
            default: // Full
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _currentSourceNode.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = rel.Id.Value };
                _buffer[2] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
        }
    }

    public void Dispose() { _source.Dispose(); }
}

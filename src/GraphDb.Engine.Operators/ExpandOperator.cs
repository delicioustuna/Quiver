using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

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

    // Manual relationship chain tracking (avoids storing ref struct RelationshipEnumerator)
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
        _source.Open(tx);
        _currentSourceNode = NodeId.Invalid;
        _nextRelId = RelationshipId.Invalid;
    }

    public bool MoveNext()
    {
        while (true)
        {
            // Walk the relationship chain of the current source node
            while (_nextRelId.IsValid)
            {
                var rel = _tx!.Relationships.Read(_nextRelId);

                // Advance to next in chain before deciding to emit or skip
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

            // No more relationships for current node — advance source
            if (!_source.MoveNext()) return false;
            _currentSourceNode = new NodeId(_source.Current[_sourceNodeColumn].LongValue);
            _nextRelId = _tx!.Nodes.Read(_currentSourceNode).FirstRelationshipId;
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

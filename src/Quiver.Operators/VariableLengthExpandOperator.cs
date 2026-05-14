using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// Variable-length path expansion: emits (startNode, endNode) for each node reachable
/// within [minHops, maxHops] hops. BFS with visited-set to prevent cycles.
/// Start node is emitted when minHops == 0.
/// </summary>
public sealed class VariableLengthExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly int _minHops;
    private readonly int _maxHops;

    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("startNode", TupleSlotType.NodeId),
        new ColumnDefinition("endNode",   TupleSlotType.NodeId)]);

    private NodeId _startNode;
    private Queue<(NodeId node, int depth)>? _frontier;
    private HashSet<long>? _visited;

    public VariableLengthExpandOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        int minHops,
        int maxHops)
    {
        if (minHops < 0) throw new ArgumentOutOfRangeException(nameof(minHops));
        if (maxHops < minHops) throw new ArgumentOutOfRangeException(nameof(maxHops));
        _source = source;
        _srcCol = sourceNodeColumn;
        _dir = direction;
        _typeFilter = typeFilter;
        _minHops = minHops;
        _maxHops = maxHops;
    }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _startNode = NodeId.Invalid;
        _frontier = null;
        _visited = null;
    }

    public bool MoveNext()
    {
        while (true)
        {
            // Drain current BFS frontier.
            while (_frontier != null && _frontier.Count > 0)
            {
                var (node, depth) = _frontier.Dequeue();

                if (depth < _maxHops)
                    ExpandNeighbors(node, depth);

                if (depth >= _minHops)
                {
                    _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _startNode.Value };
                    _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = node.Value };
                    var s = Statistics;
                    s.RowsProduced++;
                    Statistics = s;
                    return true;
                }
            }

            // Advance to next source node.
            if (!_source.MoveNext()) return false;
            _startNode = new NodeId(_source.Current[_srcCol].LongValue);
            _frontier = new Queue<(NodeId, int)>();
            _visited = [_startNode.Value];
            // Enqueue start at depth 0; emit if minHops==0, expand if maxHops>0.
            _frontier.Enqueue((_startNode, 0));
        }
    }

    private void ExpandNeighbors(NodeId node, int depth)
    {
        int next = depth + 1;
        using var cursor = _tx!.Access.Expand(_tx, node, _dir, _typeFilter);
        while (cursor.MoveNext())
        {
            var nb = cursor.Neighbor;
            if (_visited!.Add(nb.Value))
                _frontier!.Enqueue((nb, next));
        }
    }

    public void Dispose() => _source.Dispose();
}

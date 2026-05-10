using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

/// <summary>
/// Variable-length path expansion: emits (startNode, endNode) for each node reachable
/// within [minHops, maxHops] hops. BFS with visited-set to prevent cycles.
/// Start node is emitted when minHops == 0.
/// </summary>
public sealed class VariableLengthExpandOperator : IPhysicalOperator
{
    private const int AdjBuf = 512;

    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly int _minHops;
    private readonly int _maxHops;

    private ITransaction? _tx;
    private IAdjacencyBlockStore? _adjStore;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];
    private readonly AdjacencyEntry[] _adjBuf = new AdjacencyEntry[AdjBuf];

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
        _adjStore = tx.AdjacencyBlocks;
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

        if (_adjStore != null && _adjStore.HasBlock(node))
        {
            int n = _adjStore.ReadEdges(node, _dir, _typeFilter, _adjBuf);
            if (n < AdjBuf)
            {
                for (int i = 0; i < n; i++)
                {
                    var nb = _adjBuf[i].NeighborId;
                    if (_visited!.Add(nb.Value))
                        _frontier!.Enqueue((nb, next));
                }
                return;
            }
            // Buffer full → fall back to linked list for accuracy.
        }

        var relId = _tx!.Nodes.Read(node).FirstRelationshipId;
        while (relId.IsValid)
        {
            var rel = _tx.Relationships.Read(relId);
            relId = rel.Source == node ? rel.SourceNext : rel.TargetNext;
            bool ok = (!_typeFilter.HasValue || rel.Type == _typeFilter.Value) &&
                      _dir switch
                      {
                          Direction.Outgoing => rel.Source == node,
                          Direction.Incoming => rel.Target == node,
                          _ => true,
                      };
            var nb = rel.Source == node ? rel.Target : rel.Source;
            if (ok && _visited!.Add(nb.Value))
                _frontier!.Enqueue((nb, next));
        }
    }

    public void Dispose() => _source.Dispose();
}

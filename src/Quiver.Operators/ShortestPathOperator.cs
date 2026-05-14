using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// BFS-based shortest path. For each (sourceNode, targetNode) pair from the input,
/// emits (source, target, distance). Pairs with no path within maxDistance are skipped.
/// BFS runs to completion inside MoveNext() for each pair.
/// </summary>
public sealed class ShortestPathOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly int _tgtCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly long _maxDistance;

    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[3];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("source",   TupleSlotType.NodeId),
        new ColumnDefinition("target",   TupleSlotType.NodeId),
        new ColumnDefinition("distance", TupleSlotType.Int64)]);

    public ShortestPathOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        int targetNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        long maxDistance = long.MaxValue)
    {
        _source = source;
        _srcCol = sourceNodeColumn;
        _tgtCol = targetNodeColumn;
        _dir = direction;
        _typeFilter = typeFilter;
        _maxDistance = maxDistance;
    }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
    }

    public bool MoveNext()
    {
        while (_source.MoveNext())
        {
            var src = new NodeId(_source.Current[_srcCol].LongValue);
            var tgt = new NodeId(_source.Current[_tgtCol].LongValue);
            long dist = FindShortestPath(src, tgt);
            if (dist < 0) continue;

            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = src.Value };
            _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = tgt.Value };
            _buffer[2] = new TupleSlot { Type = TupleSlotType.Int64, LongValue = dist };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    private long FindShortestPath(NodeId src, NodeId tgt)
    {
        if (src == tgt) return 0;

        var dist = new Dictionary<long, long> { [src.Value] = 0 };
        var queue = new Queue<NodeId>();
        queue.Enqueue(src);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            long d = dist[node.Value];
            if (d >= _maxDistance) continue;

            using var cursor = _tx!.Access.Expand(_tx, node, _dir, _typeFilter);
            while (cursor.MoveNext())
            {
                var nb = cursor.Neighbor;
                if (dist.TryAdd(nb.Value, d + 1))
                {
                    if (nb == tgt) return d + 1;
                    queue.Enqueue(nb);
                }
            }
        }
        return -1;
    }

    public void Dispose() => _source.Dispose();
}

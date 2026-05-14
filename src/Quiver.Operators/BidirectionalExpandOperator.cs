using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// Bidirectional BFS shortest path. More efficient than ShortestPathOperator for long paths.
/// For each (sourceNode, targetNode) pair, emits (source, target, distance) when a path exists.
/// Expands the forward frontier from source (in <paramref name="direction"/>) and the backward
/// frontier from target (in the reverse direction) alternately.
/// </summary>
public sealed class BidirectionalExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly int _tgtCol;
    private readonly Direction _fwdDir;
    private readonly Direction _bwdDir;
    private readonly RelationshipTypeId? _typeFilter;

    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[3];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("source",   TupleSlotType.NodeId),
        new ColumnDefinition("target",   TupleSlotType.NodeId),
        new ColumnDefinition("distance", TupleSlotType.Int64)]);

    public BidirectionalExpandOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        int targetNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter)
    {
        _source = source;
        _srcCol = sourceNodeColumn;
        _tgtCol = targetNodeColumn;
        _fwdDir = direction;
        _bwdDir = direction switch
        {
            Direction.Outgoing => Direction.Incoming,
            Direction.Incoming => Direction.Outgoing,
            _ => Direction.Both,
        };
        _typeFilter = typeFilter;
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
            long dist = FindShortestPathBidir(src, tgt);
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

    private long FindShortestPathBidir(NodeId src, NodeId tgt)
    {
        if (src == tgt) return 0;

        // fwdDist[v] = BFS distance from src; bwdDist[v] = BFS distance from tgt (backward).
        var fwdDist = new Dictionary<long, long> { [src.Value] = 0 };
        var bwdDist = new Dictionary<long, long> { [tgt.Value] = 0 };
        var fwdFrontier = new List<NodeId> { src };
        var bwdFrontier = new List<NodeId> { tgt };
        var scratch = new List<NodeId>();
        long bestDist = long.MaxValue;

        while (fwdFrontier.Count > 0 || bwdFrontier.Count > 0)
        {
            long fLevel = fwdFrontier.Count > 0 ? fwdDist[fwdFrontier[0].Value] : long.MaxValue / 2;
            long bLevel = bwdFrontier.Count > 0 ? bwdDist[bwdFrontier[0].Value] : long.MaxValue / 2;

            // Pruning: expanding either side now produces paths >= fLevel+1+bLevel or fLevel+bLevel+1.
            if (bestDist != long.MaxValue && fLevel + bLevel + 1 >= bestDist) break;

            if (fwdFrontier.Count > 0 && fLevel <= bLevel)
            {
                scratch.Clear();
                foreach (var node in fwdFrontier)
                    ExpandInto(node, _fwdDir, fwdDist, scratch);
                foreach (var nb in scratch)
                    if (bwdDist.TryGetValue(nb.Value, out long bd))
                        bestDist = Math.Min(bestDist, fwdDist[nb.Value] + bd);
                (fwdFrontier, scratch) = (scratch, fwdFrontier);
            }
            else if (bwdFrontier.Count > 0)
            {
                scratch.Clear();
                foreach (var node in bwdFrontier)
                    ExpandInto(node, _bwdDir, bwdDist, scratch);
                foreach (var nb in scratch)
                    if (fwdDist.TryGetValue(nb.Value, out long fd))
                        bestDist = Math.Min(bestDist, fd + bwdDist[nb.Value]);
                (bwdFrontier, scratch) = (scratch, bwdFrontier);
            }
            else break;
        }

        return bestDist == long.MaxValue ? -1 : bestDist;
    }

    private void ExpandInto(NodeId node, Direction dir, Dictionary<long, long> dist, List<NodeId> next)
    {
        long nextDist = dist[node.Value] + 1;
        using var cursor = _tx!.Access.Expand(_tx, node, dir, _typeFilter);
        while (cursor.MoveNext())
        {
            var nb = cursor.Neighbor;
            if (dist.TryAdd(nb.Value, nextDist))
                next.Add(nb);
        }
    }

    public void Dispose() => _source.Dispose();
}

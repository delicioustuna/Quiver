using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 双方向 BFS 最短経路。長いパスでは <see cref="ShortestPathOperator"/> より効率的。
/// 各 (sourceVertex, targetVertex) ペアについて、経路が存在すれば (source, target, distance) を放出する。
/// ソースから前方 (引数 direction 方向) と、ターゲットから後方 (逆方向) を交互に展開する。
/// </summary>
internal sealed class BidirectionalExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly int _tgtCol;
    private readonly Direction _fwdDir;
    private readonly Direction _bwdDir;
    private readonly EdgeTypeId? _typeFilter;

    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[3];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("source",   TupleSlotType.VertexId),
        new ColumnDefinition("target",   TupleSlotType.VertexId),
        new ColumnDefinition("distance", TupleSlotType.Int64)]);

    public BidirectionalExpandOperator(
        IPhysicalOperator source,
        int sourceVertexColumn,
        int targetVertexColumn,
        Direction direction,
        EdgeTypeId? typeFilter)
    {
        _source = source;
        _srcCol = sourceVertexColumn;
        _tgtCol = targetVertexColumn;
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
            var src = new VertexId(_source.Current[_srcCol].LongValue);
            var tgt = new VertexId(_source.Current[_tgtCol].LongValue);
            long dist = FindShortestPathBidir(src, tgt);
            if (dist < 0) continue;

            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = src.Value };
            _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = tgt.Value };
            _buffer[2] = new TupleSlot { Type = TupleSlotType.Int64, LongValue = dist };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    private long FindShortestPathBidir(VertexId src, VertexId tgt)
    {
        if (src == tgt) return 0;

        // fwdDist[v] = src から v への BFS 距離、bwdDist[v] = tgt から v への BFS 距離 (後方)。
        // 距離マップのキーは slot 同一性 (Sequence)。
        var fwdDist = new Dictionary<long, long> { [src.Sequence] = 0 };
        var bwdDist = new Dictionary<long, long> { [tgt.Sequence] = 0 };
        var fwdFrontier = new List<VertexId> { src };
        var bwdFrontier = new List<VertexId> { tgt };
        var scratch = new List<VertexId>();
        long bestDist = long.MaxValue;

        while (fwdFrontier.Count > 0 || bwdFrontier.Count > 0)
        {
            long fLevel = fwdFrontier.Count > 0 ? fwdDist[fwdFrontier[0].Sequence] : long.MaxValue / 2;
            long bLevel = bwdFrontier.Count > 0 ? bwdDist[bwdFrontier[0].Sequence] : long.MaxValue / 2;

            // 枝刈り: ここから両側のどちらを展開しても、得られる経路長は fLevel+1+bLevel または fLevel+bLevel+1 以上になる。
            if (bestDist != long.MaxValue && fLevel + bLevel + 1 >= bestDist) break;

            if (fwdFrontier.Count > 0 && fLevel <= bLevel)
            {
                scratch.Clear();
                foreach (var vertex in fwdFrontier)
                    ExpandInto(vertex, _fwdDir, fwdDist, scratch);
                foreach (var nb in scratch)
                    if (bwdDist.TryGetValue(nb.Sequence, out long bd))
                        bestDist = Math.Min(bestDist, fwdDist[nb.Sequence] + bd);
                (fwdFrontier, scratch) = (scratch, fwdFrontier);
            }
            else if (bwdFrontier.Count > 0)
            {
                scratch.Clear();
                foreach (var vertex in bwdFrontier)
                    ExpandInto(vertex, _bwdDir, bwdDist, scratch);
                foreach (var nb in scratch)
                    if (fwdDist.TryGetValue(nb.Sequence, out long fd))
                        bestDist = Math.Min(bestDist, fd + bwdDist[nb.Sequence]);
                (bwdFrontier, scratch) = (scratch, bwdFrontier);
            }
            else break;
        }

        return bestDist == long.MaxValue ? -1 : bestDist;
    }

    private void ExpandInto(VertexId vertex, Direction dir, Dictionary<long, long> dist, List<VertexId> next)
    {
        long nextDist = dist[vertex.Sequence] + 1;
        using var cursor = _tx!.Access.Expand(_tx, vertex, dir, _typeFilter);
        while (cursor.MoveNext())
        {
            var nb = cursor.Neighbor;
            if (dist.TryAdd(nb.Sequence, nextDist))
                next.Add(nb);
        }
    }

    public void Dispose() => _source.Dispose();
}

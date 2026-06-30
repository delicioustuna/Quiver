using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// BFS ベースの最短経路オペレータ。入力の各 <c>(source, target)</c> ペアについて
/// <c>(source, target, distance)</c> を放出する。maxDistance 以内に経路が無いペアはスキップする。
/// </summary>
/// <remarks>
/// 1 ホップ展開は <see cref="OneHopExpansion"/> 経由で <see cref="ShortestPathKernel"/> に委譲する。
/// カーネルはターゲット到達時に <see cref="IGraphKernel{TState}.VisitNeighbor"/> から <c>false</c>
/// を返し、現在の frontier を完走せずに外側ループを短絡させる。
/// </remarks>
internal sealed class ShortestPathOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly int _tgtCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly long _maxDistance;

    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[3];
    private ShortestPathState _state;
    private ShortestPathKernel? _kernel;

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
        _state = default;
        _kernel = new ShortestPathKernel(_maxDistance);
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

        _state.Target = tgt;
        _kernel!.Initialize(src, ref _state);

        while (_state.Queue!.Count > 0)
        {
            var (node, depth) = _state.Queue.Dequeue();
            if (!_kernel.ShouldContinue(depth, in _state)) continue;

            if (!OneHopExpansion.Expand(_tx!, node, _dir, _typeFilter, depth, _kernel, ref _state))
                return _state.FoundDistance;
        }
        return -1;
    }

    public void Dispose() => _source.Dispose();

    /// <summary>
    /// <see cref="ShortestPathOperator"/> のペアごとの BFS 状態。
    /// <see cref="Target"/> はオペレータが <see cref="ShortestPathKernel.Initialize"/>
    /// 呼び出し前にリセットし、カーネルが早期終了の検出と
    /// <see cref="FoundDistance"/> への距離書き込みに使う。
    /// </summary>
    internal struct ShortestPathState
    {
        public Queue<(NodeId Node, int Depth)>? Queue;
        public Dictionary<long, long>? Dist;
        public NodeId Target;
        public long FoundDistance;
    }

    private sealed class ShortestPathKernel(long maxDistance) : IGraphKernel<ShortestPathState>
    {
        public void Initialize(NodeId source, ref ShortestPathState s)
        {
            s.Queue ??= new Queue<(NodeId, int)>();
            s.Queue.Clear();
            s.Dist ??= new Dictionary<long, long>();
            s.Dist.Clear();
            s.Dist[source.Sequence] = 0; // 距離マップのキーは slot 同一性 (Sequence)
            s.Queue.Enqueue((source, 0));
            s.FoundDistance = -1;
        }

        public bool VisitNeighbor(
            NodeId source, NodeId target, RelationshipId rel,
            long weightRaw, int depth, ref ShortestPathState s)
        {
            long nextDist = depth + 1;
            if (!s.Dist!.TryAdd(target.Sequence, nextDist))
                return true;

            if (target == s.Target)
            {
                s.FoundDistance = nextDist;
                return false;
            }
            s.Queue!.Enqueue((target, (int)nextDist));
            return true;
        }

        public bool ShouldContinue(int depth, in ShortestPathState s) => depth < maxDistance;
    }
}

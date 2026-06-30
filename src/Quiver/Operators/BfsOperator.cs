using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// BFS 走査演算子。各ソースノードについて maxDepth 以内の到達可能ノードごとに
/// (startNode, endNode, depth) を放出する。起点自身は放出しない。
/// depth 列は <see cref="FrontierLimitOperator"/> との連携のため Int64。
/// <para>
/// 既定は逐次実行 (maxParallelism = 1)。-1 または 2 以上を渡すと
/// Parallel.ForEach による並列実行に切り替わる (ベクトル埋め込み生成など、
/// 独立したソースノードを大量に処理するバッチ向け)。
/// </para>
/// <para>
/// 1 ホップ展開は <see cref="OneHopExpansion"/> + 内部 <see cref="IGraphKernel{TState}"/>
/// に委譲し、カーソル / visited セットのロジックを
/// <see cref="VariableLengthExpandOperator"/>・<see cref="ShortestPathOperator"/>・
/// <see cref="ParallelBfsOperator"/> と共有する。
/// </para>
/// </summary>
internal sealed class BfsOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly int _maxDepth;
    private readonly int _maxParallelism;

    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[3];
    private NodeId _startNode;
    private FrontierKernelState _state;
    private BfsKernel? _kernel;

    private ParallelBfsOperator? _parallel;

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("startNode", TupleSlotType.NodeId),
        new ColumnDefinition("endNode",   TupleSlotType.NodeId),
        new ColumnDefinition("depth",     TupleSlotType.Int64)]);

    /// <param name="maxParallelism">
    /// 1 (既定) = 逐次。-1 = 全コア使用。それ以外の正値は並列度の上限。
    /// 並列モードは独立したソースノードが多いバッチ処理 (埋め込み生成等) 向け。
    /// </param>
    public BfsOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        int maxDepth,
        int maxParallelism = 1)
    {
        if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        _source = source;
        _srcCol = sourceNodeColumn;
        _dir = direction;
        _typeFilter = typeFilter;
        _maxDepth = maxDepth;
        _maxParallelism = maxParallelism;
    }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics => _parallel?.Statistics ?? _seqStats;
    private OperatorStatistics _seqStats;
    public TupleRef Current => _parallel != null ? _parallel.Current : new(_buffer);

    public void Open(ITransaction tx)
    {
        if (_maxParallelism != 1)
        {
            _parallel = new ParallelBfsOperator(_source, _srcCol, _dir, _typeFilter, _maxDepth, _maxParallelism);
            _parallel.Open(tx);
            return;
        }
        _tx = tx;
        _source.Open(tx);
        _startNode = NodeId.Invalid;
        _state = default;
        _kernel = new BfsKernel(_maxDepth);
    }

    public bool MoveNext()
    {
        if (_parallel != null) return _parallel.MoveNext();

        while (true)
        {
            while (_state.Frontier is { Count: > 0 } frontier)
            {
                var (node, depth) = frontier.Dequeue();

                if (_kernel!.ShouldContinue(depth, in _state))
                    OneHopExpansion.Expand(_tx!, node, _dir, _typeFilter, depth, _kernel, ref _state);

                if (depth > 0)
                {
                    _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _startNode.Value };
                    _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = node.Value };
                    _buffer[2] = new TupleSlot { Type = TupleSlotType.Int64, LongValue = depth };
                    _seqStats.RowsProduced++;
                    return true;
                }
            }

            if (!_source.MoveNext()) return false;
            _startNode = new NodeId(_source.Current[_srcCol].LongValue);
            _kernel!.Initialize(_startNode, ref _state);
        }
    }

    public void Dispose()
    {
        _parallel?.Dispose();
        if (_parallel == null) _source.Dispose();
    }

    /// <summary>
    /// 未訪問の近傍を共有 <see cref="FrontierKernelState"/> に積む BFS カーネル。
    /// <c>maxDepth</c> を超えたら下降停止; 行の放出はオペレータ側が担う。
    /// </summary>
    private sealed class BfsKernel(int maxDepth) : IGraphKernel<FrontierKernelState>
    {
        public void Initialize(NodeId source, ref FrontierKernelState s)
        {
            s.Frontier ??= new Queue<(NodeId, int)>();
            s.Frontier.Clear();
            s.Visited ??= new HashSet<long>();
            s.Visited.Clear();
            s.Visited.Add(source.Sequence); // 内部 dedup は slot 同一性 (Sequence) で判定する
            s.Frontier.Enqueue((source, 0));
        }

        public bool VisitNeighbor(
            NodeId source, NodeId target, RelationshipId rel,
            long weightRaw, int depth, ref FrontierKernelState s)
        {
            if (s.Visited!.Add(target.Sequence))
                s.Frontier!.Enqueue((target, depth + 1));
            return true;
        }

        public bool ShouldContinue(int depth, in FrontierKernelState s) => depth < maxDepth;
    }
}

/// <summary>
/// frontier 駆動オペレータ群 (<see cref="BfsOperator"/>・
/// <see cref="VariableLengthExpandOperator"/>・<see cref="ParallelBfsOperator"/>)
/// の共有 BFS 状態。オペレータ内に by-value で保持し、各カーネル呼び出しに
/// <c>ref</c> で渡す。Queue / HashSet の内部バッファは Clear 後も再利用される。
/// </summary>
internal struct FrontierKernelState
{
    public Queue<(NodeId Node, int Depth)>? Frontier;
    public HashSet<long>? Visited;
}

using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// BFS traversal. For each source node, emits (startNode, endNode, depth)
/// for every reachable node up to maxDepth. Start node itself is not emitted.
/// Depth column is Int64 for use with FrontierLimitOperator.
///
/// By default runs sequentially (maxParallelism = 1). Pass maxParallelism = -1
/// or a positive value greater than 1 to enable parallel execution via
/// Parallel.ForEach — useful for batch workloads such as vector embedding
/// generation where many independent source nodes are processed at once.
///
/// PW-13: per-hop expansion is delegated to <see cref="OneHopExpansion"/>
/// driven by a private <see cref="IGraphKernel{TState}"/> implementation,
/// so the cursor / visited-set logic is shared with
/// <see cref="VariableLengthExpandOperator"/>, <see cref="ShortestPathOperator"/>
/// and <see cref="ParallelBfsOperator"/>.
/// </summary>
public sealed class BfsOperator : IPhysicalOperator
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
    /// 1 (default) = sequential. -1 = use all available cores. Any other positive
    /// value caps the degree of parallelism. Parallel mode is intended for batch
    /// workloads (e.g. embedding generation) with many independent source nodes.
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
    /// PW-13: BFS kernel that pushes unseen neighbours into the shared
    /// <see cref="FrontierKernelState"/>. Stops descending past
    /// <c>maxDepth</c>; the operator emits the actual rows.
    /// </summary>
    private sealed class BfsKernel(int maxDepth) : IGraphKernel<FrontierKernelState>
    {
        public void Initialize(NodeId source, ref FrontierKernelState s)
        {
            s.Frontier ??= new Queue<(NodeId, int)>();
            s.Frontier.Clear();
            s.Visited ??= new HashSet<long>();
            s.Visited.Clear();
            s.Visited.Add(source.Value);
            s.Frontier.Enqueue((source, 0));
        }

        public bool VisitNeighbor(
            NodeId source, NodeId target, RelationshipId rel,
            long weightRaw, int depth, ref FrontierKernelState s)
        {
            if (s.Visited!.Add(target.Value))
                s.Frontier!.Enqueue((target, depth + 1));
            return true;
        }

        public bool ShouldContinue(int depth, in FrontierKernelState s) => depth < maxDepth;
    }
}

/// <summary>
/// PW-13: Shared BFS state for the family of frontier-driven operators
/// (<see cref="BfsOperator"/>, <see cref="VariableLengthExpandOperator"/>,
/// <see cref="ParallelBfsOperator"/>). Held by-value in the operator and
/// passed by <c>ref</c> to each kernel call so that Queue / HashSet
/// references can be reused across source nodes (their internal buffers
/// survive Clear).
/// </summary>
public struct FrontierKernelState
{
    public Queue<(NodeId Node, int Depth)>? Frontier;
    public HashSet<long>? Visited;
}

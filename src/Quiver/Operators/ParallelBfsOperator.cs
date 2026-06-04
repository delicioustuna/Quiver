using System.Collections.Concurrent;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// PW-7: Parallel BFS implementation. Used internally by BfsOperator when
/// maxParallelism != 1. Not part of the public API.
///
/// Collects all source nodes from upstream, then runs independent BFS from each
/// source simultaneously using Parallel.ForEach. Each parallel task owns private
/// state (frontier queue, visited HashSet, expand cursor), so no
/// synchronization is needed during traversal — only result collection uses a
/// ConcurrentBag.
///
/// The underlying stores are safe to read concurrently: TxNodeStore.Read() and
/// TxRelationshipStore.Read() delegate directly to the inner stores without write
/// locks, and PagedFile.PinForRead() serializes briefly on _poolLock only to
/// load/pin the frame, then releases before data is copied.
///
/// Schema: (startNode NodeId, endNode NodeId, depth Int64).
///
/// PW-13: per-task BFS reuses <see cref="OneHopExpansion"/> with a private
/// <see cref="ParallelKernel"/>. Each task owns an isolated
/// <see cref="FrontierKernelState"/>, so no kernel state crosses task
/// boundaries.
/// </summary>
internal sealed class ParallelBfsOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly int _maxDepth;
    private readonly int _maxParallelism;

    private ITransaction? _tx;
    private List<(NodeId start, NodeId end, int depth)>? _results;
    private int _resultIdx;
    private readonly TupleSlot[] _buffer = new TupleSlot[3];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("startNode", TupleSlotType.NodeId),
        new ColumnDefinition("endNode",   TupleSlotType.NodeId),
        new ColumnDefinition("depth",     TupleSlotType.Int64)]);

    /// <param name="maxParallelism">
    /// Maximum degree of parallelism passed to Parallel.ForEach.
    /// -1 (default) lets the runtime choose based on available cores.
    /// </param>
    public ParallelBfsOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        int maxDepth,
        int maxParallelism = -1)
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
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _results = null;
        _resultIdx = 0;
    }

    public bool MoveNext()
    {
        if (_results == null)
            RunParallel();

        if (_resultIdx >= _results!.Count) return false;

        var (start, end, depth) = _results[_resultIdx++];
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = start.Value };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = end.Value };
        _buffer[2] = new TupleSlot { Type = TupleSlotType.Int64,  LongValue = depth };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    private void RunParallel()
    {
        // Drain upstream sequentially to gather all source nodes.
        var sources = new List<NodeId>();
        while (_source.MoveNext())
            sources.Add(new NodeId(_source.Current[_srcCol].LongValue));

        var bag = new ConcurrentBag<(NodeId start, NodeId end, int depth)>();
        var tx = _tx!;
        var dir = _dir;
        var typeFilter = _typeFilter;
        var maxDepth = _maxDepth;
        var kernel = new ParallelKernel(maxDepth);

        Parallel.ForEach(
            sources,
            new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism },
            // Per-task state factory: each worker thread owns its own frontier /
            // visited buffers and recycles them across the source nodes that the
            // partitioner hands to it.
            () => new FrontierKernelState(),
            (source, _, state) =>
            {
                kernel.Initialize(source, ref state);

                while (state.Frontier!.Count > 0)
                {
                    var (node, depth) = state.Frontier.Dequeue();

                    if (depth > 0)
                        bag.Add((source, node, depth));

                    if (kernel.ShouldContinue(depth, in state))
                        OneHopExpansion.Expand(tx, node, dir, typeFilter, depth, kernel, ref state);
                }
                return state;
            },
            _ => { });

        _results = [.. bag];
    }

    public void Dispose() => _source.Dispose();

    /// <summary>
    /// PW-13: stateless BFS kernel shared by every parallel worker. Frontier /
    /// visited live in the worker-local <see cref="FrontierKernelState"/> the
    /// task factory hands in, so no kernel field is mutated concurrently.
    /// </summary>
    private sealed class ParallelKernel(int maxDepth) : IGraphKernel<FrontierKernelState>
    {
        public void Initialize(NodeId source, ref FrontierKernelState s)
        {
            s.Frontier ??= new Queue<(NodeId, int)>();
            s.Frontier.Clear();
            s.Visited ??= new HashSet<long>();
            s.Visited.Clear();
            s.Visited.Add(source.Sequence); // ARCH-5b: 内部 dedup は slot 同一性 (Sequence)
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

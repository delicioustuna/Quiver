using System.Collections.Concurrent;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// PW-7: Parallel BFS implementation. Used internally by BfsOperator when
/// maxParallelism != 1. Not part of the public API.
///
/// Collects all source nodes from upstream, then runs independent BFS from each
/// source simultaneously using Parallel.ForEach. Each parallel task owns private
/// state (frontier queue, visited HashSet, adjacency entry buffer), so no
/// synchronization is needed during traversal — only result collection uses a
/// ConcurrentBag.
///
/// The underlying stores are safe to read concurrently: TxNodeStore.Read() and
/// TxRelationshipStore.Read() delegate directly to the inner stores without write
/// locks, and PagedFile.PinForRead() serializes briefly on _poolLock only to
/// load/pin the frame, then releases before data is copied.
///
/// Schema: (startNode NodeId, endNode NodeId, depth Int64).
/// </summary>
internal sealed class ParallelBfsOperator : IPhysicalOperator
{
    private const int AdjBuf = 512;

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
        var adjStore = tx.AdjacencyBlocks;
        var dir = _dir;
        var typeFilter = _typeFilter;
        var maxDepth = _maxDepth;

        Parallel.ForEach(
            sources,
            new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism },
            source =>
            {
                // All mutable state is thread-local — no cross-task synchronization needed.
                var adjBuf = new AdjacencyEntry[AdjBuf];
                var visited = new HashSet<long> { source.Value };
                var frontier = new Queue<(NodeId node, int depth)>();
                frontier.Enqueue((source, 0));

                while (frontier.Count > 0)
                {
                    var (node, depth) = frontier.Dequeue();

                    if (depth > 0)
                        bag.Add((source, node, depth));

                    if (depth >= maxDepth) continue;

                    int next = depth + 1;

                    // Fast path: contiguous adjacency block.
                    if (adjStore != null && adjStore.HasBlock(node))
                    {
                        int n = adjStore.ReadEdges(node, dir, typeFilter, adjBuf);
                        if (n < AdjBuf)
                        {
                            for (int i = 0; i < n; i++)
                            {
                                var nb = adjBuf[i].NeighborId;
                                if (visited.Add(nb.Value))
                                    frontier.Enqueue((nb, next));
                            }
                            continue;
                        }
                        // Buffer exactly full → degree may exceed it; fall through to linked-list.
                    }

                    // Linked-list fallback: walk the relationship chain.
                    var relId = tx.Nodes.Read(node).FirstRelationshipId;
                    while (relId.IsValid)
                    {
                        var rel = tx.Relationships.Read(relId);
                        relId = rel.Source == node ? rel.SourceNext : rel.TargetNext;
                        bool ok = (!typeFilter.HasValue || rel.Type == typeFilter.Value) &&
                                  dir switch
                                  {
                                      Direction.Outgoing => rel.Source == node,
                                      Direction.Incoming => rel.Target == node,
                                      _ => true,
                                  };
                        var nb = rel.Source == node ? rel.Target : rel.Source;
                        if (ok && visited.Add(nb.Value))
                            frontier.Enqueue((nb, next));
                    }
                }
            });

        _results = [.. bag];
    }

    public void Dispose() => _source.Dispose();
}

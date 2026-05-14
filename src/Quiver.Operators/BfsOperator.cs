using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// BFS traversal. For each source node, emits (startNode, endNode, depth)
/// for every reachable node up to maxDepth. Start node itself is not emitted.
/// Depth column is Int64 for use with FrontierLimitOperator.
///
/// By default runs sequentially (maxParallelism = 1). Pass maxParallelism = -1
/// or a positive value greater than 1 to enable parallel execution via
/// Parallel.ForEach — useful for batch workloads such as vector embedding
/// generation where many independent source nodes are processed at once.
/// </summary>
public sealed class BfsOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly int _maxDepth;
    private readonly int _maxParallelism;

    // Sequential state
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[3];
    private NodeId _startNode;
    private Queue<(NodeId node, int depth)>? _frontier;
    private HashSet<long>? _visited;

    // Parallel delegate (created lazily when maxParallelism != 1)
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
        _frontier = null;
        _visited = null;
    }

    public bool MoveNext()
    {
        if (_parallel != null) return _parallel.MoveNext();

        while (true)
        {
            while (_frontier != null && _frontier.Count > 0)
            {
                var (node, depth) = _frontier.Dequeue();

                if (depth < _maxDepth)
                    ExpandNeighbors(node, depth);

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
            _frontier = new Queue<(NodeId, int)>();
            _visited = [_startNode.Value];
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

    public void Dispose()
    {
        _parallel?.Dispose();
        if (_parallel == null) _source.Dispose();
    }
}

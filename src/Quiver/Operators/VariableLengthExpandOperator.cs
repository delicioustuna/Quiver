using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// Variable-length path expansion: emits (startNode, endNode) for each node reachable
/// within [minHops, maxHops] hops. BFS with visited-set to prevent cycles.
/// Start node is emitted when minHops == 0.
///
/// PW-13: per-hop expansion is delegated to <see cref="OneHopExpansion"/> via
/// a private <see cref="IGraphKernel{TState}"/>; frontier / visited bookkeeping
/// is shared with <see cref="BfsOperator"/> through <see cref="FrontierKernelState"/>.
/// </summary>
public sealed class VariableLengthExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly int _minHops;
    private readonly int _maxHops;

    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("startNode", TupleSlotType.NodeId),
        new ColumnDefinition("endNode",   TupleSlotType.NodeId)]);

    private NodeId _startNode;
    private FrontierKernelState _state;
    private VarLenKernel? _kernel;

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
        _source.Open(tx);
        _startNode = NodeId.Invalid;
        _state = default;
        _kernel = new VarLenKernel(_maxHops);
    }

    public bool MoveNext()
    {
        while (true)
        {
            // Drain current BFS frontier.
            while (_state.Frontier is { Count: > 0 } frontier)
            {
                var (node, depth) = frontier.Dequeue();

                if (_kernel!.ShouldContinue(depth, in _state))
                    OneHopExpansion.Expand(_tx!, node, _dir, _typeFilter, depth, _kernel, ref _state);

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

            if (!_source.MoveNext()) return false;
            _startNode = new NodeId(_source.Current[_srcCol].LongValue);
            _kernel!.Initialize(_startNode, ref _state);
        }
    }

    public void Dispose() => _source.Dispose();

    private sealed class VarLenKernel(int maxHops) : IGraphKernel<FrontierKernelState>
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

        public bool ShouldContinue(int depth, in FrontierKernelState s) => depth < maxHops;
    }
}

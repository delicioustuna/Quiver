using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// PW-17: One-hop expansion that scans the relationship store sequentially and
/// probes each record against a pre-built <see cref="FrontierSet"/> of source
/// nodes. Wins over the per-node linked-list / adjacency-block path when the
/// frontier is large relative to total edges, because chasing N independent
/// linked lists destroys page locality whereas the sequential scan touches each
/// relationship page exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Output schema matches <see cref="ExpandOperator"/> for the chosen
/// <see cref="ExpandOutputMode"/>, so the operator is drop-in compatible with
/// downstream <c>Filter</c> / <c>Project</c> nodes.
/// </para>
/// <para>
/// The source operator is fully drained in <see cref="Open"/> to build the
/// frontier set; this is the same trade-off the algorithm itself makes — the
/// scan only pays off when the frontier is large, and building the set is O(N)
/// regardless.
/// </para>
/// </remarks>
internal sealed class RelationshipScanExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly ExpandOutputMode _outputMode;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;

    private ITransaction? _tx;
    private FrontierSet? _frontier;
    private IEnumerator<RelationshipId>? _scanEnumerator;

    public RelationshipScanExpandOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        ExpandOutputMode outputMode)
    {
        _source = source;
        _sourceNodeColumn = sourceNodeColumn;
        _direction = direction;
        _typeFilter = typeFilter;
        _outputMode = outputMode;
        (_buffer, _schema) = outputMode switch
        {
            ExpandOutputMode.NeighborOnly => (new TupleSlot[1], new TupleSchema([
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
            ExpandOutputMode.NeighborAndRel => (new TupleSlot[2], new TupleSchema([
                new ColumnDefinition("rel", TupleSlotType.RelationshipId),
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
            _ => (new TupleSlot[3], new TupleSchema([
                new ColumnDefinition("source", TupleSlotType.NodeId),
                new ColumnDefinition("rel", TupleSlotType.RelationshipId),
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
        };
    }

    public TupleSchema Schema => _schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);

        // Drain source into a frontier set. We collect ids twice (list + set)
        // only to decide bitmap vs hashset; the list is dropped after Build.
        var ids = new List<long>(64);
        long max = -1;
        while (_source.MoveNext())
        {
            long v = _source.Current[_sourceNodeColumn].LongValue;
            ids.Add(v);
            if (v > max) max = v;
        }
        _frontier = FrontierSet.Build(ids, max);
        _scanEnumerator = tx.Relationships.Scan().GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_scanEnumerator == null || _frontier == null) return false;
        while (_scanEnumerator.MoveNext())
        {
            var relId = _scanEnumerator.Current;
            var rel = _tx!.Relationships.Read(relId);

            if (_typeFilter.HasValue && rel.Type != _typeFilter.Value) continue;

            // PW-17 stat: every relationship the scan inspects is counted as a
            // logical read. Caller can compare against the per-node path's
            // RowsProduced to see scan locality wins.
            var s = Statistics;
            s.RelationshipScanRecords++;

            NodeId source, neighbor;
            switch (_direction)
            {
                case Direction.Outgoing:
                    if (!_frontier.Contains(rel.Source)) { Statistics = s; continue; }
                    source = rel.Source; neighbor = rel.Target;
                    break;
                case Direction.Incoming:
                    if (!_frontier.Contains(rel.Target)) { Statistics = s; continue; }
                    source = rel.Target; neighbor = rel.Source;
                    break;
                default: // Both
                    if (_frontier.Contains(rel.Source))
                    {
                        source = rel.Source; neighbor = rel.Target;
                    }
                    else if (_frontier.Contains(rel.Target))
                    {
                        source = rel.Target; neighbor = rel.Source;
                    }
                    else { Statistics = s; continue; }
                    break;
            }

            BuildOutput(source, neighbor, relId);
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    private void BuildOutput(NodeId source, NodeId neighbor, RelationshipId relId)
    {
        switch (_outputMode)
        {
            case ExpandOutputMode.NeighborOnly:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
            case ExpandOutputMode.NeighborAndRel:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = relId.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
            default:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = source.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = relId.Value };
                _buffer[2] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
        }
    }

    public void Dispose()
    {
        _scanEnumerator?.Dispose();
        _scanEnumerator = null;
        _frontier = null;
        _source.Dispose();
    }
}

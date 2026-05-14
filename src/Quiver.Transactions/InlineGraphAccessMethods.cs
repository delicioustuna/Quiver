using System.Text;
using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Transactions;

/// <summary>
/// Fallback <see cref="IGraphAccessMethods"/> used when a transaction is
/// created without an explicit backend access-method implementation (e.g. unit
/// tests that wire a <see cref="TransactionManager"/> by hand). Performs the
/// same logic that operators historically did inline — adjacency block fast
/// path with linked-list fallback. Does not track <see cref="AdjacencyFallbackCount"/>;
/// real backends should supply their own implementation for that.
/// </summary>
public sealed class InlineGraphAccessMethods : IGraphAccessMethods
{
    public static readonly InlineGraphAccessMethods Instance = new();

    public long AdjacencyFallbackCount => 0;

    public IEnumerable<NodeId> ScanNodes(ITransaction tx, LabelId? label = null)
    {
        if (!label.HasValue) return tx.Nodes.Scan();
        return ScanByLabel(tx, label.Value);
    }

    private static IEnumerable<NodeId> ScanByLabel(ITransaction tx, LabelId label)
    {
        foreach (var id in tx.Nodes.Scan())
        {
            if (tx.Nodes.Read(id).Label == label)
                yield return id;
        }
    }

    public IEnumerable<NodeId> SeekNodesByIndex(ITransaction tx, string indexName, PropertyValue key)
    {
        // PropertyValue is a ref struct, so we cannot hold it across a yield.
        // Materialise the underlying long enumerable up front, then wrap.
        IEnumerable<long> ids = key.Type switch
        {
            PropertyValueType.Int32 or PropertyValueType.Int64 or PropertyValueType.Bool =>
                tx.Indexes.CreateInt64Index(indexName).SeekValues(key.Int64Value),
            PropertyValueType.Double =>
                tx.Indexes.CreateDoubleIndex(indexName).SeekValues(key.DoubleValue),
            PropertyValueType.String =>
                tx.Indexes.CreateStringIndex(indexName)
                    .SeekValues(Encoding.UTF8.GetString(key.Utf8StringValue)),
            _ => [],
        };
        return WrapNodeIds(ids);
    }

    private static IEnumerable<NodeId> WrapNodeIds(IEnumerable<long> ids)
    {
        foreach (var v in ids) yield return new NodeId(v);
    }

    public ExpandCursor Expand(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter)
        => new InlineExpandCursor(tx, source, direction, typeFilter);

    public double EstimateExpandCardinality(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter)
    {
        double count = 0;
        var relId = tx.Nodes.Read(source).FirstRelationshipId;
        while (relId.IsValid)
        {
            var rel = tx.Relationships.Read(relId);
            bool typeOk = !typeFilter.HasValue || rel.Type == typeFilter.Value;
            bool dirOk = direction switch
            {
                Direction.Outgoing => rel.Source == source,
                Direction.Incoming => rel.Target == source,
                _ => true,
            };
            if (typeOk && dirOk) count++;
            relId = rel.Source == source ? rel.SourceNext : rel.TargetNext;
        }
        return count;
    }
}

internal sealed class InlineExpandCursor : ExpandCursor
{
    private const int AdjBufferSize = 8192;

    private readonly ITransaction _tx;
    private readonly NodeId _source;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;

    private readonly AdjacencyEntry[] _adjBuffer = new AdjacencyEntry[AdjBufferSize];
    private int _adjCount;
    private int _adjIdx;
    private bool _usingAdj;
    private bool _opened;
    private RelationshipId _nextRelId;
    private NodeId _neighbor;
    private RelationshipId _relId;

    internal InlineExpandCursor(ITransaction tx, NodeId source, Direction direction, RelationshipTypeId? typeFilter)
    {
        _tx = tx;
        _source = source;
        _direction = direction;
        _typeFilter = typeFilter;
        _nextRelId = RelationshipId.Invalid;
        _neighbor = NodeId.Invalid;
        _relId = RelationshipId.Invalid;
    }

    public override NodeId Neighbor => _neighbor;
    public override RelationshipId Relationship => _relId;

    public override bool MoveNext()
    {
        if (!_opened) { Open(); _opened = true; }

        if (_usingAdj)
        {
            if (_adjIdx < _adjCount)
            {
                var entry = _adjBuffer[_adjIdx++];
                _neighbor = entry.NeighborId;
                _relId = entry.RelId;
                return true;
            }
            return false;
        }

        while (_nextRelId.IsValid)
        {
            var rel = _tx.Relationships.Read(_nextRelId);
            var thisRel = _nextRelId;
            _nextRelId = rel.Source == _source ? rel.SourceNext : rel.TargetNext;

            bool typeOk = !_typeFilter.HasValue || rel.Type == _typeFilter.Value;
            bool dirOk = _direction switch
            {
                Direction.Outgoing => rel.Source == _source,
                Direction.Incoming => rel.Target == _source,
                _ => true,
            };
            if (typeOk && dirOk)
            {
                _neighbor = rel.Source == _source ? rel.Target : rel.Source;
                _relId = thisRel;
                return true;
            }
        }
        return false;
    }

    private void Open()
    {
        var adj = _tx.AdjacencyBlocks;
        if (adj != null && adj.HasBlock(_source))
        {
            int n = adj.ReadEdges(_source, _direction, _typeFilter, _adjBuffer);
            if (n < AdjBufferSize)
            {
                _adjCount = n;
                _adjIdx = 0;
                _usingAdj = true;
                return;
            }
            // Buffer filled exactly; fall back. InlineGraphAccessMethods does not
            // track fallback counts — that's a backend-specific concern.
        }
        _usingAdj = false;
        _nextRelId = _tx.Nodes.Read(_source).FirstRelationshipId;
    }
}

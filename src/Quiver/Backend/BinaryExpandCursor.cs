using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

// ExpandCursor is now defined in Quiver.Transactions (BA-3); this class extends it.

/// <summary>
/// Binary-backend expand cursor. When the source node has an adjacency block it walks
/// the block chain via <see cref="IAdjacencyBlockStore.OpenCursor"/>, which never falls
/// back mid-iteration regardless of degree (PW-8). Falls back to the relationship linked
/// list only when no adjacency block was built for the node — that path is tracked by
/// <see cref="BinaryGraphAccessMethods.AdjacencyFallbackCount"/> so diagnostics can surface it.
/// </summary>
internal sealed class BinaryExpandCursor : ExpandCursor
{
    private readonly ITransaction _tx;
    private readonly NodeId _source;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly BinaryGraphAccessMethods _owner;

    private AdjacencyCursor? _adjCursor;
    private bool _usingAdj;
    private bool _opened;

    private RelationshipId _nextRelId;
    private NodeId _neighbor;
    private RelationshipId _relId;

    internal BinaryExpandCursor(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter,
        BinaryGraphAccessMethods owner)
    {
        _tx = tx;
        _source = source;
        _direction = direction;
        _typeFilter = typeFilter;
        _owner = owner;
        _nextRelId = RelationshipId.Invalid;
        _neighbor = NodeId.Invalid;
        _relId = RelationshipId.Invalid;
    }

    public override NodeId Neighbor => _neighbor;
    public override RelationshipId Relationship => _relId;
    public override long WeightRaw => _adjCursor?.WeightRaw ?? 0;

    public override bool MoveNext()
    {
        if (!_opened) { Open(); _opened = true; }

        if (_usingAdj)
        {
            if (_adjCursor!.MoveNext())
            {
                _neighbor = _adjCursor.Neighbor;
                _relId = _adjCursor.Relationship;
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
            _adjCursor = adj.OpenCursor(_source, _direction, _typeFilter);
            _usingAdj = true;
            return;
        }
        // No adjacency block for this source — walk the relationship linked list.
        // Bumped so diagnostics can surface "how often we missed the fast path".
        System.Threading.Interlocked.Increment(ref _owner.FallbackCountInternal);
        _usingAdj = false;
        _nextRelId = _tx.Nodes.Read(_source).FirstRelationshipId;
    }

    public override void Dispose()
    {
        _adjCursor?.Dispose();
    }
}

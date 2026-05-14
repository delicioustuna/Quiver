using System.Threading;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

// ExpandCursor is now defined in Quiver.Transactions (BA-3); this class extends it.

/// <summary>
/// Binary-backend expand cursor. Tries the contiguous adjacency block first
/// (when <see cref="ITransaction.AdjacencyBlocks"/> covers the source node and
/// the buffer isn't filled exactly); otherwise walks the relationship linked
/// list. Each linked-list fallback bumps <see cref="BinaryGraphAccessMethods"/>'s
/// fallback counter so it shows up in diagnostics.
/// </summary>
internal sealed class BinaryExpandCursor : ExpandCursor
{
    // Matches the historical ExpandOperator buffer size so behaviour is preserved.
    private const int AdjBufferSize = 8192;

    private readonly ITransaction _tx;
    private readonly NodeId _source;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly BinaryGraphAccessMethods _owner;

    private readonly AdjacencyEntry[] _adjBuffer = new AdjacencyEntry[AdjBufferSize];
    private int _adjCount;
    private int _adjIdx;
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
            // Buffer filled exactly — degree may exceed it; fall back to linked list.
            Interlocked.Increment(ref _owner.FallbackCountInternal);
        }
        _usingAdj = false;
        _nextRelId = _tx.Nodes.Read(_source).FirstRelationshipId;
    }
}

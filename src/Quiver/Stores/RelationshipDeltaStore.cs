using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Storage.Records;

/// <summary>
/// Immutable adjacency base にまだ fold されていない relationship delta を、
/// transaction snapshot に従って合成するための小さな coordinator。
/// </summary>
internal sealed class RelationshipDeltaStore
{
    public static RelationshipDeltaStore Shared { get; } = new();

    private readonly PersistentRelationshipDeltaStore? _persistent;

    public RelationshipDeltaStore()
    {
    }

    public RelationshipDeltaStore(PersistentRelationshipDeltaStore persistent)
    {
        _persistent = persistent;
    }

    public AdjacencyCursor OpenCursor(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter,
        long baseRelHwm)
    {
        if (_persistent != null)
            return new PersistentValidatingCursor(
                tx,
                source,
                direction,
                _persistent.OpenCursor(source, direction, typeFilter, baseRelHwm),
                direction == Direction.Incoming
                    ? _persistent.OpenCursor(source, Direction.Outgoing, typeFilter, baseRelHwm)
                    : null);

        var firstRelId = tx.Nodes.Read(source).FirstRelationshipId;
        return new DeltaCursor(tx, source, direction, typeFilter, baseRelHwm, firstRelId);
    }

    /// <summary>
    /// 隣接 base が利用できないときの row-path cursor を開く。
    /// persistent delta は base 以後の追加分だけなので、base 自体が無効な場合は
    /// node chain (physical relationship Sequence) を走査する必要がある。
    /// </summary>
    public AdjacencyCursor OpenRowCursor(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter)
    {
        var firstRelId = tx.Nodes.Read(source).FirstRelationshipId;
        return new DeltaCursor(tx, source, direction, typeFilter, baseRelHwm: 0, firstRelId);
    }

    public int Count(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter,
        long baseRelHwm,
        int limit = int.MaxValue)
    {
        using var cursor = OpenCursor(tx, source, direction, typeFilter, baseRelHwm);
        int count = 0;
        while (count < limit && cursor.MoveNext())
            count++;
        return count;
    }

    private sealed class DeltaCursor : AdjacencyCursor
    {
        private readonly ITransaction _tx;
        private readonly NodeId _source;
        private readonly Direction _direction;
        private readonly RelationshipTypeId? _typeFilter;
        private readonly long _baseRelHwm;
        private RelationshipId _nextRelId;
        private NodeId _neighbor = NodeId.Invalid;
        private RelationshipId _relId = RelationshipId.Invalid;
        private RelationshipTypeId _type;

        internal DeltaCursor(
            ITransaction tx,
            NodeId source,
            Direction direction,
            RelationshipTypeId? typeFilter,
            long baseRelHwm,
            RelationshipId firstRelId)
        {
            _tx = tx;
            _source = source;
            _direction = direction;
            _typeFilter = typeFilter;
            _baseRelHwm = baseRelHwm;
            _nextRelId = firstRelId;
        }

        public override NodeId Neighbor => _neighbor;
        public override RelationshipId Relationship => _relId;
        public override RelationshipTypeId Type => _type;

        public override bool MoveNext()
        {
            while (_nextRelId.IsValid)
            {
                if (_baseRelHwm > 0 && _nextRelId.Sequence < _baseRelHwm)
                    return false;

                var thisRel = _nextRelId;
                int generation = _tx.Relationships.CurrentGeneration(thisRel.Sequence);
                if (generation < 0)
                {
                    _nextRelId = RelationshipId.Invalid;
                    return false;
                }

                // node chain は physical Sequence を格納する。物理ポインタをそのまま
                // logical Read に渡さず、現行 generation を付けてから読む。
                var rel = _tx.Relationships.Read(
                    RelationshipId.Create(thisRel.Sequence, generation));
                bool sourceIsEndpoint = rel.Source.Sequence == _source.Sequence;
                _nextRelId = sourceIsEndpoint ? rel.SourceNext : rel.TargetNext;

                if (!rel.InUse)
                    continue;

                bool typeOk = !_typeFilter.HasValue || rel.Type == _typeFilter.Value;
                bool dirOk = _direction switch
                {
                    Direction.Outgoing => rel.Source.Sequence == _source.Sequence,
                    Direction.Incoming => rel.Target.Sequence == _source.Sequence,
                    _ => rel.Source.Sequence == _source.Sequence || rel.Target.Sequence == _source.Sequence,
                };
                if (!typeOk || !dirOk)
                    continue;

                _neighbor = sourceIsEndpoint ? rel.Target : rel.Source;
                _relId = rel.Id;
                _type = rel.Type;
                return true;
            }
            return false;
        }
    }

    private sealed class PersistentValidatingCursor : AdjacencyCursor
    {
        private readonly ITransaction _tx;
        private readonly NodeId _source;
        private readonly Direction _direction;
        private readonly AdjacencyCursor _primary;
        private readonly AdjacencyCursor? _selfLoopFallback;
        private AdjacencyCursor _current;
        private bool _readingSelfLoopFallback;
        private readonly HashSet<long>? _seenRelationships;
        private NodeId _neighbor = NodeId.Invalid;
        private RelationshipId _relId = RelationshipId.Invalid;
        private RelationshipTypeId _type = RelationshipTypeId.Invalid;

        internal PersistentValidatingCursor(
            ITransaction tx,
            NodeId source,
            Direction direction,
            AdjacencyCursor primary,
            AdjacencyCursor? selfLoopFallback)
        {
            _tx = tx;
            _source = source;
            _direction = direction;
            _primary = primary;
            _selfLoopFallback = selfLoopFallback;
            _current = primary;
            _seenRelationships = selfLoopFallback != null ? [] : null;
        }

        public override NodeId Neighbor => _neighbor;
        public override RelationshipId Relationship => _relId;
        public override RelationshipTypeId Type => _type;

        public override bool MoveNext()
        {
            while (true)
            {
                while (_current.MoveNext())
                {
                    RelationshipId candidate = _current.Relationship;
                    if (_seenRelationships != null && !_seenRelationships.Add(candidate.Sequence))
                        continue;

                    // persistent delta は relationship の physical Sequence だけを保持する。
                    // read 境界で現行の full ID へ戻し、世代付き stale candidate はここで除外する。
                    var materializer = new EntityIdentityMaterializer(
                        _tx.Nodes, _tx.Relationships, _tx.Hyperedges);
                    if (!materializer.TryRelationship(candidate, out var logicalRelationship))
                        continue;

                    var rel = _tx.Relationships.Read(logicalRelationship);
                    if (!rel.InUse)
                        continue;

                    bool dirOk = _direction switch
                    {
                        Direction.Outgoing => rel.Source.Sequence == _source.Sequence,
                        Direction.Incoming => rel.Target.Sequence == _source.Sequence,
                        _ => rel.Source.Sequence == _source.Sequence || rel.Target.Sequence == _source.Sequence,
                    };
                    if (!dirOk)
                        continue;

                    NodeId expectedNeighbor = rel.Source.Sequence == _source.Sequence ? rel.Target : rel.Source;
                    if (_current.Neighbor.Sequence != expectedNeighbor.Sequence
                        || _current.Type != rel.Type)
                        continue;

                    _neighbor = expectedNeighbor;
                    _relId = rel.Id;
                    _type = rel.Type;
                    return true;
                }

                if (_selfLoopFallback == null || _readingSelfLoopFallback)
                    return false;

                _readingSelfLoopFallback = true;
                _current = _selfLoopFallback;
            }
        }

        public override void Dispose()
        {
            _primary.Dispose();
            _selfLoopFallback?.Dispose();
        }
    }
}

using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Storage.Records;

/// <summary>
/// Immutable adjacency base にまだ fold されていない edge delta を、
/// transaction snapshot に従って合成するための小さな coordinator。
/// </summary>
internal sealed class EdgeDeltaStore
{
    public static EdgeDeltaStore Shared { get; } = new();

    private readonly PersistentEdgeDeltaStore? _persistent;

    public EdgeDeltaStore()
    {
    }

    public EdgeDeltaStore(PersistentEdgeDeltaStore persistent)
    {
        _persistent = persistent;
    }

    public AdjacencyCursor OpenCursor(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter,
        long baseEdgeHwm)
    {
        if (_persistent != null)
            return new PersistentValidatingCursor(
                tx,
                source,
                direction,
                _persistent.OpenCursor(source, direction, typeFilter, baseEdgeHwm),
                direction == Direction.Incoming
                    ? _persistent.OpenCursor(source, Direction.Outgoing, typeFilter, baseEdgeHwm)
                    : null);

        var firstEdgeId = tx.Vertices.Read(source).FirstEdgeId;
        return new DeltaCursor(tx, source, direction, typeFilter, baseEdgeHwm, firstEdgeId);
    }

    /// <summary>
    /// 隣接 base が利用できないときの row-path cursor を開く。
    /// persistent delta は base 以後の追加分だけなので、base 自体が無効な場合は
    /// vertex chain (physical edge Sequence) を走査する必要がある。
    /// </summary>
    public AdjacencyCursor OpenRowCursor(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter)
    {
        var firstEdgeId = tx.Vertices.Read(source).FirstEdgeId;
        return new DeltaCursor(tx, source, direction, typeFilter, baseEdgeHwm: 0, firstEdgeId);
    }

    public int Count(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter,
        long baseEdgeHwm,
        int limit = int.MaxValue)
    {
        using var cursor = OpenCursor(tx, source, direction, typeFilter, baseEdgeHwm);
        int count = 0;
        while (count < limit && cursor.MoveNext())
            count++;
        return count;
    }

    private sealed class DeltaCursor : AdjacencyCursor
    {
        private readonly ITransaction _tx;
        private readonly VertexId _source;
        private readonly Direction _direction;
        private readonly EdgeTypeId? _typeFilter;
        private readonly long _baseEdgeHwm;
        private EdgeId _nextEdgeId;
        private VertexId _neighbor = VertexId.Invalid;
        private EdgeId _edgeId = EdgeId.Invalid;
        private EdgeTypeId _type;

        internal DeltaCursor(
            ITransaction tx,
            VertexId source,
            Direction direction,
            EdgeTypeId? typeFilter,
            long baseEdgeHwm,
            EdgeId firstEdgeId)
        {
            _tx = tx;
            _source = source;
            _direction = direction;
            _typeFilter = typeFilter;
            _baseEdgeHwm = baseEdgeHwm;
            _nextEdgeId = firstEdgeId;
        }

        public override VertexId Neighbor => _neighbor;
        public override EdgeId Edge => _edgeId;
        public override EdgeTypeId Type => _type;

        public override bool MoveNext()
        {
            while (_nextEdgeId.IsValid)
            {
                if (_baseEdgeHwm > 0 && _nextEdgeId.Sequence < _baseEdgeHwm)
                    return false;

                var thisEdge = _nextEdgeId;
                int generation = _tx.Edges.CurrentGeneration(thisEdge.Sequence);
                if (generation < 0)
                {
                    _nextEdgeId = EdgeId.Invalid;
                    return false;
                }

                // vertex chain は physical Sequence を格納する。物理ポインタをそのまま
                // logical Read に渡さず、現行 generation を付けてから読む。
                var edge = _tx.Edges.Read(
                    EdgeId.Create(thisEdge.Sequence, generation));
                bool sourceIsEndpoint = edge.Source.Sequence == _source.Sequence;
                _nextEdgeId = sourceIsEndpoint ? edge.SourceNext : edge.TargetNext;

                if (!edge.InUse)
                    continue;

                bool typeOk = !_typeFilter.HasValue || edge.Type == _typeFilter.Value;
                bool dirOk = _direction switch
                {
                    Direction.Outgoing => edge.Source.Sequence == _source.Sequence,
                    Direction.Incoming => edge.Target.Sequence == _source.Sequence,
                    _ => edge.Source.Sequence == _source.Sequence || edge.Target.Sequence == _source.Sequence,
                };
                if (!typeOk || !dirOk)
                    continue;

                _neighbor = sourceIsEndpoint ? edge.Target : edge.Source;
                _edgeId = edge.Id;
                _type = edge.Type;
                return true;
            }
            return false;
        }
    }

    private sealed class PersistentValidatingCursor : AdjacencyCursor
    {
        private readonly ITransaction _tx;
        private readonly VertexId _source;
        private readonly Direction _direction;
        private readonly AdjacencyCursor _primary;
        private readonly AdjacencyCursor? _selfLoopFallback;
        private AdjacencyCursor _current;
        private bool _readingSelfLoopFallback;
        private readonly HashSet<long>? _seenEdges;
        private VertexId _neighbor = VertexId.Invalid;
        private EdgeId _edgeId = EdgeId.Invalid;
        private EdgeTypeId _type = EdgeTypeId.Invalid;

        internal PersistentValidatingCursor(
            ITransaction tx,
            VertexId source,
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
            _seenEdges = selfLoopFallback != null ? [] : null;
        }

        public override VertexId Neighbor => _neighbor;
        public override EdgeId Edge => _edgeId;
        public override EdgeTypeId Type => _type;

        public override bool MoveNext()
        {
            while (true)
            {
                while (_current.MoveNext())
                {
                    EdgeId candidate = _current.Edge;
                    if (_seenEdges != null && !_seenEdges.Add(candidate.Sequence))
                        continue;

                    // persistent delta は edge の physical Sequence だけを保持する。
                    // read 境界で現行の full ID へ戻し、世代付き stale candidate はここで除外する。
                    var materializer = new EntityIdentityMaterializer(
                        _tx.Vertices, _tx.Edges, _tx.Nexuses);
                    if (!materializer.TryEdge(candidate, out var logicalEdge))
                        continue;

                    var edge = _tx.Edges.Read(logicalEdge);
                    if (!edge.InUse)
                        continue;

                    bool dirOk = _direction switch
                    {
                        Direction.Outgoing => edge.Source.Sequence == _source.Sequence,
                        Direction.Incoming => edge.Target.Sequence == _source.Sequence,
                        _ => edge.Source.Sequence == _source.Sequence || edge.Target.Sequence == _source.Sequence,
                    };
                    if (!dirOk)
                        continue;

                    VertexId expectedNeighbor = edge.Source.Sequence == _source.Sequence ? edge.Target : edge.Source;
                    if (_current.Neighbor.Sequence != expectedNeighbor.Sequence
                        || _current.Type != edge.Type)
                        continue;

                    _neighbor = expectedNeighbor;
                    _edgeId = edge.Id;
                    _type = edge.Type;
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

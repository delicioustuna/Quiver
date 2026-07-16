using System.Text;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

/// <summary>
/// トランザクション生成時に明示的なバックエンド実装が指定されなかった場合のフォールバック
/// <see cref="IGraphAccessMethods"/>。隣接ブロック高速パスとリンクリストフォールバックを
/// インラインで実行する。<see cref="AdjacencyFallbackCount"/> は追跡しない。
/// </summary>
internal sealed class InlineGraphAccessMethods : IGraphAccessMethods
{
    public static readonly InlineGraphAccessMethods Instance = new();

    public long AdjacencyFallbackCount => 0;

    public IEnumerable<VertexId> ScanVertices(ITransaction tx, LabelId? label = null)
    {
        if (!label.HasValue) return tx.Vertices.Scan();
        return ScanByLabel(tx, label.Value);
    }

    private static IEnumerable<VertexId> ScanByLabel(ITransaction tx, LabelId label)
    {
        foreach (var id in tx.Vertices.Scan())
        {
            if (tx.Vertices.Read(id).Label == label)
                yield return id;
        }
    }

    public IEnumerable<VertexId> SeekVerticesByIndex(ITransaction tx, string indexName, PropertyValue key)
    {
        // PropertyValue は ref struct のため、yield を跨いで保持できない。
        // 下層の long 列挙を先にマテリアライズし、それをラップする。
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
        // パック値を世代照合しつつ VertexId へ unpack し、slot 再利用の stale 参照を弾く。
        return IndexValueResolver.ResolveLiveVertexIds(ids, tx.Vertices);
    }

    public ExpandCursor Expand(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter)
        => new InlineExpandCursor(tx, source, direction, typeFilter);

    public double EstimateExpandCardinality(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter)
    {
        var materializer = new EntityIdentityMaterializer(tx.Vertices);
        if (!materializer.TryVertex(source, out source))
            return 0;

        double count = 0;
        var edgeId = tx.Vertices.Read(source).FirstEdgeId;
        while (edgeId.IsValid)
        {
            var edge = tx.Edges.Read(edgeId);
            bool typeOk = !typeFilter.HasValue || edge.Type == typeFilter.Value;
            bool dirOk = direction switch
            {
                Direction.Outgoing => edge.Source.Sequence == source.Sequence,
                Direction.Incoming => edge.Target.Sequence == source.Sequence,
                _ => true,
            };
            if (typeOk && dirOk) count++;
            edgeId = edge.Source.Sequence == source.Sequence ? edge.SourceNext : edge.TargetNext;
        }
        return count;
    }
}

internal sealed class InlineExpandCursor : ExpandCursor
{
    private readonly ITransaction _tx;
    private VertexId _source;
    private readonly Direction _direction;
    private readonly EdgeTypeId? _typeFilter;

    private AdjacencyCursor? _adjCursor;
    private bool _usingAdj;
    private bool _opened;
    private bool _validSource;
    private EdgeId _nextEdgeId;
    private VertexId _neighbor;
    private EdgeId _edgeId;

    internal InlineExpandCursor(ITransaction tx, VertexId source, Direction direction, EdgeTypeId? typeFilter)
    {
        _tx = tx;
        _source = source;
        _direction = direction;
        _typeFilter = typeFilter;
        _nextEdgeId = EdgeId.Invalid;
        _neighbor = VertexId.Invalid;
        _edgeId = EdgeId.Invalid;
    }

    public override VertexId Neighbor => _neighbor;
    public override EdgeId Edge => _edgeId;

    public override bool MoveNext()
    {
        if (!_opened) { Open(); _opened = true; }
        if (!_validSource) return false;

        if (_usingAdj)
        {
            while (_adjCursor!.MoveNext())
            {
                var relation = _tx!.Edges.Read(_adjCursor.Edge);
                if (!relation.InUse)
                    continue;
                _neighbor = relation.Source.Sequence == _source.Sequence ? relation.Target : relation.Source;
                _edgeId = relation.Id;
                return true;
            }
            return false;
        }

        while (_nextEdgeId.IsValid)
        {
            int generation = _tx.Edges.CurrentGeneration(_nextEdgeId.Sequence);
            if (generation < 0)
            {
                _nextEdgeId = EdgeId.Invalid;
                return false;
            }

            // vertex chain は physical Sequence を保持するため、logical Read の直前で
            // 現行 generation を付与する。
            var edge = _tx.Edges.Read(
                EdgeId.Create(_nextEdgeId.Sequence, generation));
            var thisEdge = _nextEdgeId;
            bool sourceIsEndpoint = edge.Source.Sequence == _source.Sequence;
            _nextEdgeId = sourceIsEndpoint ? edge.SourceNext : edge.TargetNext;

            if (!edge.InUse)
                continue;

            bool typeOk = !_typeFilter.HasValue || edge.Type == _typeFilter.Value;
            bool dirOk = _direction switch
            {
                Direction.Outgoing => edge.Source.Sequence == _source.Sequence,
                Direction.Incoming => edge.Target.Sequence == _source.Sequence,
                _ => true,
            };
            if (typeOk && dirOk)
            {
                _neighbor = sourceIsEndpoint ? edge.Target : edge.Source;
                _edgeId = edge.Id;
                return true;
            }
        }
        return false;
    }

    private void Open()
    {
        var materializer = new EntityIdentityMaterializer(_tx.Vertices);
        if (!materializer.TryVertex(_source, out _source))
            return;
        _validSource = true;

        var adj = _tx.AdjacencyBlocks;
        if (adj != null && adj.HasBlock(_source))
        {
            _adjCursor = adj.OpenCursor(_source, _direction, _typeFilter);
            _usingAdj = true;
            return;
        }
        _usingAdj = false;
        _nextEdgeId = _tx.Vertices.Read(_source).FirstEdgeId;
    }

    public override void Dispose() => _adjCursor?.Dispose();
}

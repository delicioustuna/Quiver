using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Transactions;

internal sealed class TxEdgeStore : IEdgeStore
{
    private readonly IEdgeStore _inner;
    private readonly TransactionId _transactionId;
    private readonly TxVertexStore _vertices;
    private readonly SnapshotState _snapshot;
    private readonly VersionVisible _visibility;
    private readonly bool _isReadOnly;
    private readonly PersistentEdgeDeltaStore? _deltaStore;

    internal TxEdgeStore(
        IEdgeStore inner,
        TransactionId transactionId,
        TxVertexStore vertices,
        in SnapshotState snapshot,
        CommittedTxRegistry committed,
        bool isReadOnly,
        PersistentEdgeDeltaStore? deltaStore = null)
    {
        _inner = inner;
        _transactionId = transactionId;
        _vertices = vertices;
        _snapshot = snapshot;
        _visibility = (xmin, xmax) => Visibility.IsVisible(xmin, xmax, in _snapshot, _transactionId);
        _isReadOnly = isReadOnly;
        _deltaStore = deltaStore;
    }

    public long InUseCount => _inner.InUseCount;

    public int CurrentGeneration(long localId)
        => _inner.CurrentGeneration(localId);

    public EdgeId Create(
        IVertexStore _,
        VertexId source,
        VertexId target,
        EdgeTypeId type)
    {
        EnsureWritable();
        EdgeId edgeId = _inner is ITransactionEdgeStore store
            ? store.Create(_vertices, source, target, type, _transactionId)
            : _inner.Create(_vertices, source, target, type);
        if (_deltaStore is not null)
        {
            _deltaStore.Append(source, Direction.Outgoing, edgeId, target, type);
            if (source != target)
                _deltaStore.Append(target, Direction.Incoming, edgeId, source, type);
        }
        return edgeId;
    }

    public void Delete(IVertexStore _, EdgeId edgeId)
    {
        EnsureWritable();
        if (_inner is ITransactionEdgeStore store)
            store.Delete(_vertices, edgeId, _transactionId);
        else
            _inner.Delete(_vertices, edgeId);
    }

    public EdgeReadHandle Read(EdgeId edgeId)
    {
        EdgeReadHandle raw = _inner is ITransactionEdgeStore store
            ? store.Read(edgeId, _visibility)
            : _inner.Read(edgeId);
        if (!raw.InUse) return raw;

        var materializer = new EntityIdentityMaterializer(_vertices);
        if (!materializer.TryVertexReferenceFromVisibleOwner(raw.Source, out VertexId source)
            || !materializer.TryVertexReferenceFromVisibleOwner(raw.Target, out VertexId target))
        {
            return new EdgeReadHandle(
                raw.Id,
                inUse: false,
                raw.Source,
                raw.Target,
                raw.Type,
                raw.SourcePrev,
                raw.SourceNext,
                raw.TargetPrev,
                raw.TargetNext,
                raw.FirstPropertyRef);
        }

        return new EdgeReadHandle(
            raw.Id,
            inUse: true,
            source,
            target,
            raw.Type,
            raw.SourcePrev,
            raw.SourceNext,
            raw.TargetPrev,
            raw.TargetNext,
            raw.FirstPropertyRef);
    }

    public EdgeWriteHandle Write(EdgeId edgeId)
    {
        EnsureWritable();
        return _inner.Write(edgeId);
    }

    public EdgeEnumerator EnumerateNeighbors(
        VertexId vertexId,
        IVertexStore vertexStore)
    {
        EdgeId firstEdgeId = _vertices.Read(vertexId).FirstEdgeId;
        return new EdgeEnumerator(this, _vertices, vertexId, firstEdgeId);
    }

    public EdgeEnumerator EnumerateNeighbors(
        VertexId vertexId,
        IVertexStore vertexStore,
        EdgeTypeId type,
        Direction direction)
    {
        EdgeId firstEdgeId = _vertices.Read(vertexId).FirstEdgeId;
        return new EdgeEnumerator(this, _vertices, vertexId, firstEdgeId, type, direction);
    }

    public IEnumerable<EdgeId> Scan()
    {
        return _inner is ITransactionEdgeStore store
            ? store.Scan(_visibility)
            : _inner.Scan();
    }

    public IEnumerable<EdgeId> Lookup(
        VertexId source,
        VertexId target,
        EdgeTypeId type)
        => _inner.Lookup(source, target, type);

    public PropertyCursor EnumerateProperties(
        EdgeId edgeId,
        IPropertyStore overflowStore)
    {
        EdgeReadHandle edge = Read(edgeId);
        if (!edge.InUse)
            return new PropertyCursor(overflowStore, default, PropertyVersionRef.Invalid);
        var ownerId = edgeId.Generation == 0 ? edge.Id : edgeId;
        return overflowStore.Enumerate(EntityRef.From(ownerId), edge.FirstPropertyRef);
    }

    private void EnsureWritable()
    {
        if (_isReadOnly)
            throw new TransactionException("Cannot mutate edges in a read-only transaction.");
    }
}

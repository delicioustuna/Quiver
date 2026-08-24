using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Transactions;

internal sealed class TxVertexStore : IVertexStore
{
    private readonly IVertexStore _inner;
    private readonly TransactionId _transactionId;
    private readonly SnapshotState _snapshot;
    private readonly VersionVisible _visibility;
    private readonly bool _isReadOnly;

    internal TxVertexStore(
        IVertexStore inner,
        TransactionId transactionId,
        in SnapshotState snapshot,
        CommittedTxRegistry committed,
        bool isReadOnly)
    {
        _inner = inner;
        _transactionId = transactionId;
        _snapshot = snapshot;
        _visibility = (xmin, xmax) => Visibility.IsVisible(xmin, xmax, in _snapshot, _transactionId);
        _isReadOnly = isReadOnly;
    }

    public long InUseCount => _inner.InUseCount;

    public VertexId Allocate(LabelId labelId)
    {
        EnsureWritable();
        return _inner is ITransactionVertexStore store
            ? store.Allocate(labelId, _transactionId)
            : _inner.Allocate(labelId);
    }

    public void Free(VertexId vertexId)
    {
        EnsureWritable();
        if (_inner is ITransactionVertexStore store)
            store.Free(vertexId, _transactionId);
        else
            _inner.Free(vertexId);
    }

    public VertexReadHandle Read(VertexId vertexId)
    {
        return _inner is ITransactionVertexStore store
            ? store.Read(vertexId, _visibility)
            : _inner.Read(vertexId);
    }

    public VertexWriteHandle Write(VertexId vertexId)
    {
        EnsureWritable();
        return _inner.Write(vertexId);
    }

    public IEnumerable<VertexId> Scan()
    {
        return _inner is ITransactionVertexStore store
            ? store.Scan(_visibility)
            : _inner.Scan();
    }

    public int CurrentGeneration(long localId)
        => _inner.CurrentGeneration(localId);

    public PropertyCursor EnumerateProperties(
        VertexId vertexId,
        IPropertyStore overflowStore)
    {
        VertexReadHandle vertex = Read(vertexId);
        if (!vertex.InUse)
            return new PropertyCursor(overflowStore, default, PropertyVersionRef.Invalid);
        var ownerId = vertexId.Generation == 0 ? vertex.Id : vertexId;
        return overflowStore.Enumerate(EntityRef.From(ownerId), vertex.FirstPropertyRef);
    }

    private void EnsureWritable()
    {
        if (_isReadOnly)
            throw new TransactionException("Cannot mutate vertices in a read-only transaction.");
    }
}

using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

internal sealed class TxEdgeStore : IEdgeStore
{
    private readonly IEdgeStore _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;
    private readonly TxVertexStore _txVertices;
    private readonly LockingMode _mode;
    private readonly TimeSpan _timeout;
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    private readonly PersistentEdgeDeltaStore? _deltaStore;
    // SSN (Serializable) のときのみ非 null。read/write set 収集のみ。
    private readonly SsnContext? _ssn;

    internal TxEdgeStore(IEdgeStore inner, LockManager locks, TransactionId txId, TxVertexStore txVertices, LockingMode mode, TimeSpan timeout,
        SnapshotState snapshot = default, CommittedTxRegistry? committed = null,
        SsnContext? ssn = null,
        PersistentEdgeDeltaStore? deltaStore = null)
    {
        _inner = inner; _locks = locks; _txId = txId; _txVertices = txVertices; _mode = mode; _timeout = timeout;
        _snapshot = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _committed = committed;
        _ssn = ssn;
        _deltaStore = deltaStore;
    }

    public long InUseCount => _inner.InUseCount;

    // sidecar generation は MVCC / lock を必要としない raw metadata のため、そのまま委譲する。
    // logical materializer が raw edge Sequence を full ID に戻す境界で使用する。
    public int CurrentGeneration(long localId) => _inner.CurrentGeneration(localId);

    public EdgeId Create(IVertexStore _, VertexId source, VertexId target, EdgeTypeId type)
    {
        ActivateMvccContext();
        EdgeId edgeId = _inner.Create(_txVertices, source, target, type);
        if (_deltaStore != null)
        {
            _deltaStore.Append(source, Direction.Outgoing, edgeId, target, type);
            if (source != target)
                _deltaStore.Append(target, Direction.Incoming, edgeId, source, type);
        }

        return edgeId;
    }

    public void Delete(IVertexStore _, EdgeId edgeId)
    {
        // lock / SSN キーは Sequence (read-set 側 RecordRead と整合させる)。
        Acquire(edgeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(edgeId.Sequence);
        _inner.Delete(_txVertices, edgeId);
    }

    public EdgeReadHandle Read(EdgeId edgeId)
    {
        if (_mode == LockingMode.ReaderWriter)
            Acquire(edgeId.Sequence, LockMode.Shared);
        // read-set は sink 経由で _inner.Read が記録する (traversal の隣接走査も
        // EdgeEnumerator が _inner.Read を呼ぶので同経路で捕捉される)。
        ActivateMvccContext();
        var raw = _inner.Read(edgeId);
        if (!raw.InUse)
            return raw;

        var materializer = new EntityIdentityMaterializer(_txVertices);
        if (!materializer.TryVertexReferenceFromVisibleOwner(raw.Source, out var source)
            || !materializer.TryVertexReferenceFromVisibleOwner(raw.Target, out var target))
        {
            return new EdgeReadHandle(
                raw.Id, inUse: false, raw.Source, raw.Target, raw.Type,
                raw.SourcePrev, raw.SourceNext, raw.TargetPrev, raw.TargetNext,
                raw.FirstPropertyId);
        }

        return new EdgeReadHandle(
            raw.Id, inUse: true, source, target, raw.Type,
            raw.SourcePrev, raw.SourceNext, raw.TargetPrev, raw.TargetNext,
            raw.FirstPropertyId);
    }

    public EdgeWriteHandle Write(EdgeId edgeId)
    {
        Acquire(edgeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(edgeId.Sequence);
        return _inner.Write(edgeId);
    }

    public EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore vertexStore)
    {
        ActivateMvccContext();
        return _inner.EnumerateNeighbors(vertexId, vertexStore);
    }

    public EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore vertexStore, EdgeTypeId type, Direction direction)
    {
        ActivateMvccContext();
        return _inner.EnumerateNeighbors(vertexId, vertexStore, type, direction);
    }

    public IEnumerable<EdgeId> Scan()
    {
        ActivateMvccContext();
        return _inner.Scan();
    }

    // inline property は TxVertexStore と同様に lock + SSN + MVCC コンテキストでラップする。

    public bool TryGetInlineProperty(EdgeId edgeId, PropertyKeyId keyId, out PropertyValue value)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(edgeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.TryGetInlineProperty(edgeId, keyId, out value);
    }

    public bool HasInlineProperty(EdgeId edgeId, PropertyKeyId keyId)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(edgeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.HasInlineProperty(edgeId, keyId);
    }

    public bool SetInlineProperty(EdgeId edgeId, PropertyKeyId keyId, in PropertyValue value)
    {
        Acquire(edgeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(edgeId.Sequence);
        return _inner.SetInlineProperty(edgeId, keyId, in value);
    }

    public bool RemoveInlineProperty(EdgeId edgeId, PropertyKeyId keyId)
    {
        Acquire(edgeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(edgeId.Sequence);
        return _inner.RemoveInlineProperty(edgeId, keyId);
    }

    public PropertyEnumerator EnumerateProperties(EdgeId edgeId, IPropertyStore overflowStore)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(edgeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.EnumerateProperties(edgeId, overflowStore);
    }

    private void ActivateMvccContext()
    {
        if (_committed != null)
            MvccContext.Begin(_txId, _snapshot, _committed, _ssn);
    }

    // ==================== SSN write-set 収集 ====================
    // read-set は MvccContext の sink 経由でストアの Read/Scan/隣接走査が記録する。
    // Create は新規バージョン (誰も読めなかった) なので SSN write set には登録しない。

    private void SsnOnWrite(long localId)
    {
        if (_ssn == null || localId < 0) return;
        var id = new EntityId(EntityKind.Edge, localId);
        _ssn.Writes.Add(id);
        _ssn.Reads.Remove(id);
    }

    private void Acquire(long id, LockMode mode)
    {
        if (!_locks.TryAcquire(id, _txId, mode, _timeout))
        {
            string what = mode == LockMode.Exclusive ? "write" : "read";
            throw new TransactionException($"Lock timeout acquiring {what} lock on edge {id}.");
        }
    }
}

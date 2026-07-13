using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

internal sealed class TxRelationshipStore : IRelationshipStore
{
    private readonly IRelationshipStore _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;
    private readonly TxNodeStore _txNodes;
    private readonly LockingMode _mode;
    private readonly TimeSpan _timeout;
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    private readonly PersistentRelationshipDeltaStore? _deltaStore;
    // SSN (Serializable) のときのみ非 null。read/write set 収集のみ。
    private readonly SsnContext? _ssn;

    internal TxRelationshipStore(IRelationshipStore inner, LockManager locks, TransactionId txId, TxNodeStore txNodes, LockingMode mode, TimeSpan timeout,
        SnapshotState snapshot = default, CommittedTxRegistry? committed = null,
        SsnContext? ssn = null,
        PersistentRelationshipDeltaStore? deltaStore = null)
    {
        _inner = inner; _locks = locks; _txId = txId; _txNodes = txNodes; _mode = mode; _timeout = timeout;
        _snapshot = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _committed = committed;
        _ssn = ssn;
        _deltaStore = deltaStore;
    }

    public long InUseCount => _inner.InUseCount;

    // sidecar generation は MVCC / lock を必要としない raw metadata のため、そのまま委譲する。
    // logical materializer が raw relationship Sequence を full ID に戻す境界で使用する。
    public int CurrentGeneration(long localId) => _inner.CurrentGeneration(localId);

    public RelationshipId Create(INodeStore _, NodeId source, NodeId target, RelationshipTypeId type)
    {
        ActivateMvccContext();
        RelationshipId relId = _inner.Create(_txNodes, source, target, type);
        if (_deltaStore != null)
        {
            _deltaStore.Append(source, Direction.Outgoing, relId, target, type);
            if (source != target)
                _deltaStore.Append(target, Direction.Incoming, relId, source, type);
        }

        return relId;
    }

    public void Delete(INodeStore _, RelationshipId relId)
    {
        // lock / SSN キーは Sequence (read-set 側 RecordRead と整合させる)。
        Acquire(relId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(relId.Sequence);
        _inner.Delete(_txNodes, relId);
    }

    public RelationshipReadHandle Read(RelationshipId relId)
    {
        if (_mode == LockingMode.ReaderWriter)
            Acquire(relId.Sequence, LockMode.Shared);
        // read-set は sink 経由で _inner.Read が記録する (traversal の隣接走査も
        // RelationshipEnumerator が _inner.Read を呼ぶので同経路で捕捉される)。
        ActivateMvccContext();
        var raw = _inner.Read(relId);
        if (!raw.InUse)
            return raw;

        var materializer = new EntityIdentityMaterializer(_txNodes);
        if (!materializer.TryNode(raw.Source, out var source)
            || !materializer.TryNode(raw.Target, out var target))
        {
            return new RelationshipReadHandle(
                raw.Id, inUse: false, raw.Source, raw.Target, raw.Type,
                raw.SourcePrev, raw.SourceNext, raw.TargetPrev, raw.TargetNext,
                raw.FirstPropertyId);
        }

        return new RelationshipReadHandle(
            raw.Id, inUse: true, source, target, raw.Type,
            raw.SourcePrev, raw.SourceNext, raw.TargetPrev, raw.TargetNext,
            raw.FirstPropertyId);
    }

    public RelationshipWriteHandle Write(RelationshipId relId)
    {
        Acquire(relId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(relId.Sequence);
        return _inner.Write(relId);
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore)
    {
        ActivateMvccContext();
        return _inner.EnumerateNeighbors(nodeId, nodeStore);
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore, RelationshipTypeId type, Direction direction)
    {
        ActivateMvccContext();
        return _inner.EnumerateNeighbors(nodeId, nodeStore, type, direction);
    }

    public IEnumerable<RelationshipId> Scan()
    {
        ActivateMvccContext();
        return _inner.Scan();
    }

    // inline property は TxNodeStore と同様に lock + SSN + MVCC コンテキストでラップする。

    public bool TryGetInlineProperty(RelationshipId relId, PropertyKeyId keyId, out PropertyValue value)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(relId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.TryGetInlineProperty(relId, keyId, out value);
    }

    public bool HasInlineProperty(RelationshipId relId, PropertyKeyId keyId)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(relId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.HasInlineProperty(relId, keyId);
    }

    public bool SetInlineProperty(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value)
    {
        Acquire(relId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(relId.Sequence);
        return _inner.SetInlineProperty(relId, keyId, in value);
    }

    public bool RemoveInlineProperty(RelationshipId relId, PropertyKeyId keyId)
    {
        Acquire(relId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(relId.Sequence);
        return _inner.RemoveInlineProperty(relId, keyId);
    }

    public PropertyEnumerator EnumerateProperties(RelationshipId relId, IPropertyStore overflowStore)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(relId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.EnumerateProperties(relId, overflowStore);
    }

    private void ActivateMvccContext()
    {
        if (_committed != null)
            MvccContext.Begin(_txId, _snapshot, _committed, _ssn);
    }

    // ====================: SSN write-set 収集 ====================
    // read-set は MvccContext の sink 経由でストアの Read/Scan/隣接走査が記録する。
    // Create は新規バージョン (誰も読めなかった) なので SSN write set には登録しない。

    private void SsnOnWrite(long localId)
    {
        if (_ssn == null || localId < 0) return;
        var id = new EntityId(EntityKind.Relationship, localId);
        _ssn.Writes.Add(id);
        _ssn.Reads.Remove(id);
    }

    private void Acquire(long id, LockMode mode)
    {
        if (!_locks.TryAcquire(id, _txId, mode, _timeout))
        {
            string what = mode == LockMode.Exclusive ? "write" : "read";
            throw new TransactionException($"Lock timeout acquiring {what} lock on relationship {id}.");
        }
    }
}

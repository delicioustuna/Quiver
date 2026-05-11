using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Transactions;

internal sealed class TxRelationshipStore : IRelationshipStore
{
    private readonly IRelationshipStore _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;
    private readonly TxNodeStore _txNodes;

    internal TxRelationshipStore(IRelationshipStore inner, LockManager locks, TransactionId txId, TxNodeStore txNodes)
    {
        _inner = inner; _locks = locks; _txId = txId; _txNodes = txNodes;
    }

    public long InUseCount => _inner.InUseCount;

    // Pass _txNodes so node endpoint updates go through locking
    public RelationshipId Create(INodeStore _, NodeId source, NodeId target, RelationshipTypeId type)
        => _inner.Create(_txNodes, source, target, type);

    public void Delete(INodeStore _, RelationshipId relId)
    {
        AcquireLock(relId.Value);
        _inner.Delete(_txNodes, relId);
    }

    public RelationshipReadHandle Read(RelationshipId relId) => _inner.Read(relId);

    public RelationshipWriteHandle Write(RelationshipId relId)
    {
        AcquireLock(relId.Value);
        return _inner.Write(relId);
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore)
        => _inner.EnumerateNeighbors(nodeId, nodeStore);

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore, RelationshipTypeId type, Direction direction)
        => _inner.EnumerateNeighbors(nodeId, nodeStore, type, direction);

    private void AcquireLock(long id)
    {
        if (!_locks.TryAcquire(id, _txId))
            throw new TransactionException($"Lock timeout acquiring write lock on relationship {id}.");
    }
}

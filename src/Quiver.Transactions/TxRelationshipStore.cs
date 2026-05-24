using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Transactions;

internal sealed class TxRelationshipStore : IRelationshipStore
{
    private readonly IRelationshipStore _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;
    private readonly TxNodeStore _txNodes;
    private readonly LockingMode _mode;
    private readonly TimeSpan _timeout;

    internal TxRelationshipStore(IRelationshipStore inner, LockManager locks, TransactionId txId, TxNodeStore txNodes, LockingMode mode, TimeSpan timeout)
    {
        _inner = inner; _locks = locks; _txId = txId; _txNodes = txNodes; _mode = mode; _timeout = timeout;
    }

    public long InUseCount => _inner.InUseCount;

    public RelationshipId Create(INodeStore _, NodeId source, NodeId target, RelationshipTypeId type)
        => _inner.Create(_txNodes, source, target, type);

    public void Delete(INodeStore _, RelationshipId relId)
    {
        Acquire(relId.Value, LockMode.Exclusive);
        _inner.Delete(_txNodes, relId);
    }

    public RelationshipReadHandle Read(RelationshipId relId)
    {
        if (_mode == LockingMode.ReaderWriter)
            Acquire(relId.Value, LockMode.Shared);
        return _inner.Read(relId);
    }

    public RelationshipWriteHandle Write(RelationshipId relId)
    {
        Acquire(relId.Value, LockMode.Exclusive);
        return _inner.Write(relId);
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore)
        => _inner.EnumerateNeighbors(nodeId, nodeStore);

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore, RelationshipTypeId type, Direction direction)
        => _inner.EnumerateNeighbors(nodeId, nodeStore, type, direction);

    public IEnumerable<RelationshipId> Scan() => _inner.Scan();

    private void Acquire(long id, LockMode mode)
    {
        if (!_locks.TryAcquire(id, _txId, mode, _timeout))
        {
            string what = mode == LockMode.Exclusive ? "write" : "read";
            throw new TransactionException($"Lock timeout acquiring {what} lock on relationship {id}.");
        }
    }
}

using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Transactions;

internal sealed class TxNodeStore : INodeStore
{
    private readonly INodeStore _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;

    internal TxNodeStore(INodeStore inner, LockManager locks, TransactionId txId)
    {
        _inner = inner; _locks = locks; _txId = txId;
    }

    public long InUseCount => _inner.InUseCount;

    public NodeId Allocate(LabelId labelId) => _inner.Allocate(labelId);

    public void Free(NodeId nodeId)
    {
        AcquireLock(nodeId.Value);
        _inner.Free(nodeId);
    }

    public NodeReadHandle Read(NodeId nodeId) => _inner.Read(nodeId);

    public NodeWriteHandle Write(NodeId nodeId)
    {
        AcquireLock(nodeId.Value);
        return _inner.Write(nodeId);
    }

    public IEnumerable<NodeId> Scan() => _inner.Scan();

    private void AcquireLock(long id)
    {
        if (!_locks.TryAcquire(id, _txId))
            throw new TransactionException($"Lock timeout acquiring write lock on node {id}.");
    }
}

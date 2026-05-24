using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Transactions;

internal sealed class TxNodeStore : INodeStore
{
    private readonly INodeStore _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;
    private readonly LockingMode _mode;
    private readonly TimeSpan _timeout;

    internal TxNodeStore(INodeStore inner, LockManager locks, TransactionId txId, LockingMode mode, TimeSpan timeout)
    {
        _inner = inner; _locks = locks; _txId = txId; _mode = mode; _timeout = timeout;
    }

    public long InUseCount => _inner.InUseCount;

    public NodeId Allocate(LabelId labelId) => _inner.Allocate(labelId);

    public void Free(NodeId nodeId)
    {
        Acquire(nodeId.Value, LockMode.Exclusive);
        _inner.Free(nodeId);
    }

    public NodeReadHandle Read(NodeId nodeId)
    {
        // FT-24: ReaderWriter モードでは共有ロックを取り、書き込み tx と分離する。
        // ExclusiveOnly (既定) は後方互換のため無ロック。
        if (_mode == LockingMode.ReaderWriter)
            Acquire(nodeId.Value, LockMode.Shared);
        return _inner.Read(nodeId);
    }

    public NodeWriteHandle Write(NodeId nodeId)
    {
        Acquire(nodeId.Value, LockMode.Exclusive);
        return _inner.Write(nodeId);
    }

    public IEnumerable<NodeId> Scan() => _inner.Scan();

    private void Acquire(long id, LockMode mode)
    {
        if (!_locks.TryAcquire(id, _txId, mode, _timeout))
        {
            string what = mode == LockMode.Exclusive ? "write" : "read";
            throw new TransactionException($"Lock timeout acquiring {what} lock on node {id}.");
        }
    }
}

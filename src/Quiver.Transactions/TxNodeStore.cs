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
    // FT-26: per-tx MVCC コンテキスト。同一スレッドで複数 tx を交互に操作する場合、
    // thread-static MvccContext を呼出側で「使う直前に毎回」設定し直さないと
    // 別 tx の snapshot で visibility 判定が走ってしまう。Begin / End ペアは
    // Transaction.ctor / Commit/Abort に既にあるが、それは「自身が走っている間」しか
    // 効かないので、Tx 操作ごとに ambient を再アクティベートする。
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;

    internal TxNodeStore(INodeStore inner, LockManager locks, TransactionId txId, LockingMode mode, TimeSpan timeout,
        SnapshotState snapshot = default, CommittedTxRegistry? committed = null)
    {
        _inner = inner; _locks = locks; _txId = txId; _mode = mode; _timeout = timeout;
        _snapshot = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _committed = committed;
    }

    public long InUseCount => _inner.InUseCount;

    public NodeId Allocate(LabelId labelId)
    {
        ActivateMvccContext();
        return _inner.Allocate(labelId);
    }

    public void Free(NodeId nodeId)
    {
        ActivateMvccContext();
        Acquire(nodeId.Value, LockMode.Exclusive);
        _inner.Free(nodeId);
    }

    public NodeReadHandle Read(NodeId nodeId)
    {
        // FT-24: ReaderWriter モードでは共有ロックを取り、書き込み tx と分離する。
        // ExclusiveOnly (既定) は後方互換のため無ロック。
        if (_mode == LockingMode.ReaderWriter)
            Acquire(nodeId.Value, LockMode.Shared);
        ActivateMvccContext();
        return _inner.Read(nodeId);
    }

    public NodeWriteHandle Write(NodeId nodeId)
    {
        Acquire(nodeId.Value, LockMode.Exclusive);
        ActivateMvccContext();
        return _inner.Write(nodeId);
    }

    public IEnumerable<NodeId> Scan()
    {
        ActivateMvccContext();
        return _inner.Scan();
    }

    private void ActivateMvccContext()
    {
        if (_committed != null)
            MvccContext.Begin(_txId, _snapshot, _committed);
    }

    private void Acquire(long id, LockMode mode)
    {
        if (!_locks.TryAcquire(id, _txId, mode, _timeout))
        {
            string what = mode == LockMode.Exclusive ? "write" : "read";
            throw new TransactionException($"Lock timeout acquiring {what} lock on node {id}.");
        }
    }
}

using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

internal sealed class TxNodeStore : INodeStore
{
    private readonly INodeStore _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;
    private readonly LockingMode _mode;
    private readonly TimeSpan _timeout;
    // per-tx MVCC コンテキスト。同一スレッドで複数 tx を交互に操作する場合、
    // thread-static MvccContext を呼出側で「使う直前に毎回」設定し直さないと
    // 別 tx の snapshot で visibility 判定が走ってしまう。Tx 操作ごとに ambient を再アクティベートする。
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    // SSN (Serializable) のときのみ非 null。read/write hook は read/write set を
    // 収集するだけ。η/π の計算と exclusion window 判定は commit 時に commit-stamp 空間で
    // 一括実行する (Transaction.SsnValidateAndStamp)。
    private readonly SsnContext? _ssn;

    internal TxNodeStore(INodeStore inner, LockManager locks, TransactionId txId, LockingMode mode, TimeSpan timeout,
        SnapshotState snapshot = default, CommittedTxRegistry? committed = null,
        SsnContext? ssn = null)
    {
        _inner = inner; _locks = locks; _txId = txId; _mode = mode; _timeout = timeout;
        _snapshot = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _committed = committed;
        _ssn = ssn;
    }

    public long InUseCount => _inner.InUseCount;

    public NodeId Allocate(LabelId labelId)
    {
        ActivateMvccContext();
        // Allocate は新規バージョンの作成 — 誰も読めなかった entity なので
        // r:w / w:w in-edge は存在しない。SSN write set には登録しない。
        return _inner.Allocate(labelId);
    }

    public void Free(NodeId nodeId)
    {
        ActivateMvccContext();
        // lock / SSN キーは Sequence (read-set 側 RecordRead と整合させる)。
        Acquire(nodeId.Sequence, LockMode.Exclusive);
        // 論理削除は既存バージョンの上書きと同じ依存を生む。
        SsnOnWrite(nodeId.Sequence);
        _inner.Free(nodeId);
    }

    public NodeReadHandle Read(NodeId nodeId)
    {
        // ReaderWriter モードでは共有ロックを取り、書き込み tx と分離する。
        // ExclusiveOnly (既定) は後方互換のため無ロック。
        if (_mode == LockingMode.ReaderWriter)
            Acquire(nodeId.Sequence, LockMode.Shared);
        // read-set は ActivateMvccContext で登録した sink 経由で _inner.Read が記録する
        // (可視判定後の 1 件のみ)。traversal / scan も同じ _inner.Read を通るので一律捕捉される。
        ActivateMvccContext();
        return _inner.Read(nodeId);
    }

    public NodeWriteHandle Write(NodeId nodeId)
    {
        Acquire(nodeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        // early-abort は _inner.Write のミューテーション前に評価する。
        SsnOnWrite(nodeId.Sequence);
        return _inner.Write(nodeId);
    }

    public IEnumerable<NodeId> Scan()
    {
        ActivateMvccContext();
        return _inner.Scan();
    }

    // 世代照合は raw な sidecar 読み取り (MVCC / lock 不要)。そのまま委譲する。
    public int CurrentGeneration(long localId) => _inner.CurrentGeneration(localId);

    // ===== inline property (read は共有ロック / write は排他ロック + SSN write) =====

    public bool TryGetInlineProperty(NodeId nodeId, PropertyKeyId keyId, out PropertyValue value)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(nodeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.TryGetInlineProperty(nodeId, keyId, out value);
    }

    public bool HasInlineProperty(NodeId nodeId, PropertyKeyId keyId)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(nodeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.HasInlineProperty(nodeId, keyId);
    }

    public bool SetInlineProperty(NodeId nodeId, PropertyKeyId keyId, in PropertyValue value)
    {
        Acquire(nodeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(nodeId.Sequence);
        return _inner.SetInlineProperty(nodeId, keyId, in value);
    }

    public bool RemoveInlineProperty(NodeId nodeId, PropertyKeyId keyId)
    {
        Acquire(nodeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(nodeId.Sequence);
        return _inner.RemoveInlineProperty(nodeId, keyId);
    }

    public PropertyEnumerator EnumerateProperties(NodeId nodeId, IPropertyStore overflowStore)
    {
        if (_mode == LockingMode.ReaderWriter) Acquire(nodeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.EnumerateProperties(nodeId, overflowStore);
    }

    private void ActivateMvccContext()
    {
        if (_committed != null)
            MvccContext.Begin(_txId, _snapshot, _committed, _ssn);
    }

    // ==================== SSN write-set 収集 ====================
    // read-set は MvccContext の sink (SsnContext) 経由でストアの Read/Scan が記録する。

    private void SsnOnWrite(long localId)
    {
        if (_ssn == null || localId < 0) return;
        var id = new EntityId(EntityKind.Node, localId);
        _ssn.Writes.Add(id);
        _ssn.Reads.Remove(id); // self r:w 抹消
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

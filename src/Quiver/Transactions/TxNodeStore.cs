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
    // FT-26: per-tx MVCC コンテキスト。同一スレッドで複数 tx を交互に操作する場合、
    // thread-static MvccContext を呼出側で「使う直前に毎回」設定し直さないと
    // 別 tx の snapshot で visibility 判定が走ってしまう。Begin / End ペアは
    // Transaction.ctor / Commit/Abort に既にあるが、それは「自身が走っている間」しか
    // 効かないので、Tx 操作ごとに ambient を再アクティベートする。
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    // FT-33: SSN (Serializable) のときのみ非 null。read/write hook は read/write set を
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
        // FT-33: Allocate は新規バージョンの作成 — 誰も読めなかった entity なので
        // r:w / w:w in-edge は存在しない。SSN write set には登録しない。
        return _inner.Allocate(labelId);
    }

    public void Free(NodeId nodeId)
    {
        ActivateMvccContext();
        Acquire(nodeId.Value, LockMode.Exclusive);
        // FT-33: 論理削除は既存バージョンの上書きと同じ依存を生む。
        SsnOnWrite(nodeId.Value);
        _inner.Free(nodeId);
    }

    public NodeReadHandle Read(NodeId nodeId)
    {
        // FT-24: ReaderWriter モードでは共有ロックを取り、書き込み tx と分離する。
        // ExclusiveOnly (既定) は後方互換のため無ロック。
        if (_mode == LockingMode.ReaderWriter)
            Acquire(nodeId.Value, LockMode.Shared);
        // FT-33: read-set は ActivateMvccContext で登録した sink 経由で _inner.Read が記録する
        // (可視判定後の 1 件のみ)。traversal / scan も同じ _inner.Read を通るので一律捕捉される。
        ActivateMvccContext();
        return _inner.Read(nodeId);
    }

    public NodeWriteHandle Write(NodeId nodeId)
    {
        Acquire(nodeId.Value, LockMode.Exclusive);
        ActivateMvccContext();
        // FT-33: early-abort は _inner.Write のミューテーション前に評価する。
        SsnOnWrite(nodeId.Value);
        return _inner.Write(nodeId);
    }

    public IEnumerable<NodeId> Scan()
    {
        ActivateMvccContext();
        return _inner.Scan();
    }

    // ARCH-3: 世代照合は raw な sidecar 読み取り (MVCC / lock 不要)。そのまま委譲する。
    public int CurrentGeneration(long localId) => _inner.CurrentGeneration(localId);

    private void ActivateMvccContext()
    {
        if (_committed != null)
            MvccContext.Begin(_txId, _snapshot, _committed, _ssn);
    }

    // ==================== FT-33: SSN write-set 収集 ====================
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

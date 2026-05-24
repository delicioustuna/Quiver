using System.Collections.Concurrent;
using Quiver.Core;
using Quiver.Index;
using Quiver.Stores;
using Quiver.Wal;

namespace Quiver.Transactions;

internal sealed class TransactionManager : ITransactionManager
{
    private readonly IWriteAheadLog _wal;
    private readonly INodeStore _nodeStore;
    private readonly IRelationshipStore _relStore;
    private readonly IPropertyStore _propStore;
    private readonly IIndexManager _indexManager;
    private IAdjacencyBlockStore? _adjStore;
    private readonly IGraphAccessMethods _access;
    // FT-15: abort / コミット失敗時のインプロセス undo を担う。null のときは undo 無し。
    private readonly AbortUndoHandler? _undoHandler;
    private readonly LockManager _nodeLocks = new();
    private readonly LockManager _relLocks = new();
    private readonly LockManager _indexLocks = new();
    private readonly ConcurrentDictionary<long, Transaction> _active = new();
    private long _nextTxId;

    // 案A: チェックポイント契機。EnableCheckpointing で配線される。
    private Checkpointer? _checkpointer;
    private long _checkpointThresholdBytes;
    private long _lastCheckpointBytes;
    private readonly object _checkpointGate = new();

    // FT-24: ロック戦略 (ExclusiveOnly / ReaderWriter) と timeout を transaction へ流す。
    private readonly LockingMode _lockingMode;
    private readonly TimeSpan _lockTimeout;

    // FT-25: デッドロック検出器 (null = 無効)。Dispose で停止。
    private DeadlockDetector? _deadlockDetector;

    // FT-26: MVCC visibility 用。Begin / OnCommit の atomicity を保護するゲート。
    // Begin は (txId 採番 + activeAtBegin 集合のキャプチャ + _active への登録) を、
    // OnCommit は (registry.MarkCommitted + _active からの除去) を 1 ブロックで行う。
    // これにより新規 snapshot が「コミット済みかつ active には残っていない」状態を観測する。
    private readonly object _snapshotGate = new();
    private readonly CommittedTxRegistry _committed;

    public TransactionManager(
        IWriteAheadLog wal,
        INodeStore nodeStore,
        IRelationshipStore relStore,
        IPropertyStore propStore,
        IIndexManager indexManager,
        IAdjacencyBlockStore? adjStore = null,
        IGraphAccessMethods? access = null,
        AbortUndoHandler? undoHandler = null,
        LockingMode lockingMode = LockingMode.ExclusiveOnly,
        TimeSpan? lockTimeout = null,
        TimeSpan? deadlockDetectionInterval = null,
        CommittedTxRegistry? committedRegistry = null)
    {
        _wal = wal;
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _indexManager = indexManager;
        _adjStore = adjStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _undoHandler = undoHandler;
        _lockingMode = lockingMode;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        _committed = committedRegistry ?? new CommittedTxRegistry();
        // FT-26: _nextTxId は最初の Increment で 1 を返す (= Bootstrap.Value)。
        // Bootstrap は予約済みなので、最初の "ユーザ" tx が 2 から始まるよう offset しておく。
        _nextTxId = TransactionId.Bootstrap.Value + 1;
        if (deadlockDetectionInterval is { } interval && interval > TimeSpan.Zero)
        {
            _deadlockDetector = new DeadlockDetector(
                new[] { _nodeLocks, _relLocks, _indexLocks }, interval);
        }
    }

    /// <summary>
    /// FT-26: backend factory から recovery 経路で WAL を走査して構築済みの registry を注入する経路。
    /// </summary>
    internal CommittedTxRegistry CommittedRegistry => _committed;

    /// <summary>
    /// FT-26: recovery で観測した最大 TxId + 1 まで _nextTxId を巻き上げる。
    /// 既に進んでいる場合は no-op。
    /// </summary>
    internal void AdvanceNextTxIdAtLeast(long minimum)
    {
        long current = Volatile.Read(ref _nextTxId);
        while (minimum > current)
        {
            long prev = Interlocked.CompareExchange(ref _nextTxId, minimum, current);
            if (prev == current) return;
            current = prev;
        }
    }

    /// <summary>FT-25: テスト / 診断用。null のときは検出器無効。</summary>
    internal DeadlockDetector? DeadlockDetector => _deadlockDetector;

    public int ActiveCount => _active.Count;

    public long OldestActiveLsn
    {
        get
        {
            long oldest = long.MaxValue;
            foreach (var tx in _active.Values)
                if (tx.SnapshotLsn < oldest) oldest = tx.SnapshotLsn;
            return oldest == long.MaxValue ? _wal.FlushedLsn : oldest;
        }
    }

    public ITransaction Begin(IsolationLevel level = IsolationLevel.SnapshotIsolation)
    {
        // FT-26: snapshot (txId 採番 + activeAtBegin 集合キャプチャ + _active 登録) を 1 ブロックで。
        // _active 登録まで含めないと、Begin 中の自身を他の Begin の activeAtBegin に含めるかどうかが
        // 競合する。MarkCommitted も同じゲートを取るので「コミット直後の tx が見えるかどうか」は
        // ゲート取得順で決まり、Postgres SI 風になる。
        TransactionId txId;
        SnapshotState snapshot;
        long snapshotLsn = _wal.FlushedLsn;
        Transaction tx;
        lock (_snapshotGate)
        {
            txId = new TransactionId(Interlocked.Increment(ref _nextTxId) - 1);
            var activeAtBegin = new HashSet<long>(_active.Keys);
            snapshot = new SnapshotState(txId, activeAtBegin);
            _wal.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
            tx = new Transaction(txId, level, snapshotLsn,
                _wal, _nodeLocks, _relLocks, _indexLocks, this,
                _nodeStore, _relStore, _propStore, _indexManager, _adjStore, _access,
                _undoHandler, _lockingMode, _lockTimeout,
                snapshot, _committed);
            _active[txId.Value] = tx;
        }
        return tx;
    }

    /// <summary>
    /// 案A: チェックポイント契機を有効化する。<paramref name="thresholdBytes"/> 以上
    /// WAL が成長し、かつアクティブトランザクションが 0 になった時点でチェックポイントを打つ。
    /// <paramref name="thresholdBytes"/> が 0 以下のときはチェックポイントを行わない。
    /// </summary>
    internal void EnableCheckpointing(Checkpointer checkpointer, long thresholdBytes)
    {
        _checkpointer = checkpointer;
        _checkpointThresholdBytes = thresholdBytes;
        _lastCheckpointBytes = _wal.BytesWritten;
    }

    internal void OnCommit(TransactionId txId)
    {
        // FT-26: MarkCommitted と _active 除去を 1 ブロックで。新規 Begin が
        // 「コミット済みかつ active 集合に居ない」状態を観測するための atomicity。
        lock (_snapshotGate)
        {
            _committed.MarkCommitted(txId);
            _active.TryRemove(txId.Value, out _);
        }
        MaybeCheckpoint();
    }

    internal void OnAbort(TransactionId txId)
    {
        // FT-26: registry には登録しない (= visibility 判定で aborted = invisible)。
        lock (_snapshotGate)
        {
            _active.TryRemove(txId.Value, out _);
        }
    }

    /// <summary>
    /// コミット直後に呼ばれ、チェックポイント契機を満たしていれば同期的に実行する。
    ///
    /// チェックポイントはアクティブトランザクションが 0 のときにのみ打つ。これにより
    /// 全ダーティページがコミット済み (またはアボード済み — アボートはページを巻き戻さない
    /// 既存仕様) であることが保証され、シャープチェックポイントとして安全に WAL を truncate
    /// できる。未コミットトランザクションのダーティページをデータファイルへ流して
    /// しまうこともない。
    /// </summary>
    private void MaybeCheckpoint()
    {
        var checkpointer = _checkpointer;
        if (checkpointer == null || _checkpointThresholdBytes <= 0) return;

        // ロック外の安価な事前判定。
        if (!_active.IsEmpty) return;
        if (_wal.BytesWritten - Volatile.Read(ref _lastCheckpointBytes) < _checkpointThresholdBytes)
            return;

        // 同時コミットによる二重実行を防ぐ。取得できなければ他スレッドが処理中なのでスキップ。
        if (!Monitor.TryEnter(_checkpointGate)) return;
        try
        {
            // ゲート内で再判定 (TOCTOU 回避)。
            if (!_active.IsEmpty) return;
            if (_wal.BytesWritten - _lastCheckpointBytes < _checkpointThresholdBytes) return;

            checkpointer.Checkpoint();
            Volatile.Write(ref _lastCheckpointBytes, _wal.BytesWritten);
        }
        finally
        {
            Monitor.Exit(_checkpointGate);
        }
    }

    /// <summary>
    /// PW-14: swap the active adjacency store reference. Called by the backend
    /// after a compact rebuild — only safe while <see cref="ActiveCount"/> is 0
    /// since transactions snapshot the reference at <see cref="Begin"/>.
    /// </summary>
    internal void SwapAdjacencyStore(IAdjacencyBlockStore? next) => _adjStore = next;

    public void Dispose()
    {
        _deadlockDetector?.Dispose();
        _deadlockDetector = null;
    }
}

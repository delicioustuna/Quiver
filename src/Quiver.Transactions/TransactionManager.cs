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

    public TransactionManager(
        IWriteAheadLog wal,
        INodeStore nodeStore,
        IRelationshipStore relStore,
        IPropertyStore propStore,
        IIndexManager indexManager,
        IAdjacencyBlockStore? adjStore = null,
        IGraphAccessMethods? access = null)
    {
        _wal = wal;
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _indexManager = indexManager;
        _adjStore = adjStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
    }

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
        var txId = new TransactionId(Interlocked.Increment(ref _nextTxId) - 1);
        long snapshotLsn = _wal.FlushedLsn;
        _wal.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
        var tx = new Transaction(txId, level, snapshotLsn,
            _wal, _nodeLocks, _relLocks, _indexLocks, this,
            _nodeStore, _relStore, _propStore, _indexManager, _adjStore, _access);
        _active[txId.Value] = tx;
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
        _active.TryRemove(txId.Value, out _);
        MaybeCheckpoint();
    }

    internal void OnAbort(TransactionId txId) => _active.TryRemove(txId.Value, out _);

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

    public void Dispose() { }
}

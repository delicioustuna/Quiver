using Quiver.Core;
using Quiver.Index;
using Quiver.Stores;
using Quiver.Wal;

namespace Quiver.Transactions;

internal sealed class Transaction : ITransaction
{
    private readonly IWriteAheadLog _wal;
    private readonly LockManager _nodeLocks;
    private readonly LockManager _relLocks;
    private readonly LockManager _indexLocks;
    private readonly TransactionManager _manager;
    private readonly TxNodeStore _nodes;
    private readonly TxRelationshipStore _relationships;
    private readonly TxPropertyStore _properties;
    private readonly TxIndexManager _indexes;
    private readonly IAdjacencyBlockStore? _adjStore;
    private readonly IGraphAccessMethods _access;
    // FT-15: null でない場合、abort / コミット失敗時にキャプチャ済み before-image を
    // データファイルへ書き戻し、ストアメタを再ロードしてインプロセス undo を行う。
    // FT-19 以降: 索引 PagedFile も EnableWalLogging により before-image capture 対象なので、
    // 本ハンドラだけで data + index 両方の in-process abort が完結する (FT-20 で IndexUndoLog 撤去)。
    private readonly AbortUndoHandler? _undoHandler;
    private List<Action>? _onCommitted;
    private List<Action>? _onRolledBack;
    private TransactionState _state;

    // FT-23: savepoint 管理。SavepointId.Value (連番) → スタック深度 (= WalPageContext のバケット index)。
    // RollbackTo で巻き戻しても savepoint 自体は消費しないので、Value は同じレベルで再利用される。
    // ReleaseSavepoint または親 savepoint の Rollback/Release で初めて無効化される。
    private long _nextSavepointId;
    private List<(long Id, int Level)>? _savepoints;

    public TransactionId Id { get; }
    public IsolationLevel Level { get; }
    public long SnapshotLsn { get; }
    public TransactionState State => _state;

    public INodeStore Nodes => _nodes;
    public IRelationshipStore Relationships => _relationships;
    public IPropertyStore Properties => _properties;
    public IIndexManager Indexes => _indexes;
    public IAdjacencyBlockStore? AdjacencyBlocks => _adjStore;
    public IGraphAccessMethods Access => _access;

    internal Transaction(
        TransactionId id, IsolationLevel level, long snapshotLsn,
        IWriteAheadLog wal,
        LockManager nodeLocks, LockManager relLocks, LockManager indexLocks,
        TransactionManager manager,
        INodeStore nodeStore, IRelationshipStore relStore,
        IPropertyStore propStore, IIndexManager indexManager,
        IAdjacencyBlockStore? adjStore = null,
        IGraphAccessMethods? access = null,
        AbortUndoHandler? undoHandler = null,
        LockingMode lockingMode = LockingMode.ExclusiveOnly,
        TimeSpan? lockTimeout = null)
    {
        Id = id; Level = level; SnapshotLsn = snapshotLsn;
        _wal = wal;
        _nodeLocks = nodeLocks; _relLocks = relLocks; _indexLocks = indexLocks;
        _manager = manager;
        _adjStore = adjStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _undoHandler = undoHandler;
        _state = TransactionState.Active;
        var timeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        _nodes = new TxNodeStore(nodeStore, nodeLocks, id, lockingMode, timeout);
        _relationships = new TxRelationshipStore(relStore, relLocks, id, _nodes, lockingMode, timeout);
        _properties = new TxPropertyStore(propStore);
        _indexes = new TxIndexManager(indexManager, indexLocks, id, timeout);
        WalPageContext.Begin(wal, id);
    }

    public void Commit()
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot commit: transaction is not Active.");
        _state = TransactionState.Preparing;
        try
        {
            // 案C: UnpinDirty はページイメージをトランザクションバッファにコアレスするだけ。
            // ここで全 PageImage を WAL へ追記し、その後に Commit レコードを書く。
            // Commit を最後に書くことで、recovery はコミット済みトランザクションの
            // ページイメージのみを replay する。
            WalPageContext.FlushPending();
            long lsn = _wal.Append(WalRecordType.Commit, Id, ReadOnlySpan<byte>.Empty);
            _wal.FlushTo(lsn);
            WalPageContext.End();
            ReleaseAllLocks();
            _state = TransactionState.Committed;
            _manager.OnCommit(Id);
        }
        catch
        {
            // Commit failed mid-way (e.g. WAL flush failure). Surface as rollback
            // so registered OnRolledBack hooks observe a consistent outcome.
            // FT-15: roll the page changes back in place before discarding the
            // context, then mark the transaction aborted on the WAL.
            try { RollBackInPlace(); } catch { }
            try { _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty); } catch { }
            try { WalPageContext.End(); } catch { }
            try { ReleaseAllLocks(); } catch { }
            _state = TransactionState.Aborted;
            _manager.OnAbort(Id);
            FireHooks(_onRolledBack);
            throw;
        }
        FireHooks(_onCommitted);
    }

    public void Abort()
    {
        if (_state is TransactionState.Committed or TransactionState.Aborted) return;
        // FT-15: in-process undo — restore captured before-images to the data
        // files and reload page-backed store metadata, so discarded nodes /
        // edges / properties are invisible to subsequent transactions. Must run
        // before WalPageContext.End() drops the per-transaction before-image buffer.
        RollBackInPlace();
        _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty);
        WalPageContext.End();
        ReleaseAllLocks();
        _state = TransactionState.Aborted;
        _manager.OnAbort(Id);
        FireHooks(_onRolledBack);
    }

    // FT-15: apply this transaction's captured before-images in place. No-op for
    // read-only transactions and for backends without an undo handler wired.
    private void RollBackInPlace()
    {
        if (_undoHandler == null) return;
        var beforeImages = WalPageContext.CurrentBeforeImagePayloads;
        if (beforeImages.Count == 0) return;
        _undoHandler.Undo(beforeImages);
    }

    // ==================== FT-23: Savepoint ====================

    public SavepointId Savepoint(string? name = null)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot create savepoint: transaction is not Active.");
        int level = WalPageContext.PushSavepoint();
        if (level < 0)
        {
            // 書き込みコンテキストが無い (例: 読み取り専用 tx) — savepoint は no-op で良いが、
            // RollbackTo / Release の正当性チェックのため id だけは発行しておく。
            level = 0;
        }
        long id = ++_nextSavepointId;
        (_savepoints ??= new List<(long, int)>()).Add((id, level));
        return new SavepointId(id, name);
    }

    public void RollbackTo(SavepointId savepoint)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot rollback to savepoint: transaction is not Active.");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");

        int level = _savepoints![index].Level;
        // FT-23: 上位 savepoint も同時に無効化する (PostgreSQL / SQL 標準: ROLLBACK TO Sn は
        // Sn より新しい全ての savepoint も解放する)。Sn 自身は消費しない。
        _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);

        var beforeImages = WalPageContext.RollbackToSavepoint(level);
        if (_undoHandler != null && beforeImages.Count > 0)
            _undoHandler.UndoPartial(beforeImages);
    }

    public void ReleaseSavepoint(SavepointId savepoint)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot release savepoint: transaction is not Active.");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");

        int level = _savepoints![index].Level;
        // Release した savepoint より新しい savepoint も同時に無効化する (SQL 標準準拠)。
        _savepoints.RemoveRange(index, _savepoints.Count - index);
        WalPageContext.ReleaseSavepoint(level);
    }

    private int FindSavepointIndex(long id)
    {
        if (_savepoints == null) return -1;
        for (int i = 0; i < _savepoints.Count; i++)
        {
            if (_savepoints[i].Id == id) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        if (_state == TransactionState.Active) Abort();
    }

    public void OnCommitted(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        switch (_state)
        {
            case TransactionState.Committed:
                SafeInvoke(callback);
                return;
            case TransactionState.Aborted:
                return;
            default:
                (_onCommitted ??= new List<Action>()).Add(callback);
                return;
        }
    }

    public void OnRolledBack(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        switch (_state)
        {
            case TransactionState.Aborted:
                SafeInvoke(callback);
                return;
            case TransactionState.Committed:
                return;
            default:
                (_onRolledBack ??= new List<Action>()).Add(callback);
                return;
        }
    }

    private static void FireHooks(List<Action>? hooks)
    {
        if (hooks == null) return;
        for (int i = 0; i < hooks.Count; i++) SafeInvoke(hooks[i]);
    }

    private static void SafeInvoke(Action callback)
    {
        // Observer exceptions must not affect the transaction outcome.
        try { callback(); } catch { }
    }

    private void ReleaseAllLocks()
    {
        _nodeLocks.ReleaseAll(Id);
        _relLocks.ReleaseAll(Id);
        _indexLocks.ReleaseAll(Id);
    }
}

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
    private readonly AbortUndoHandler? _undoHandler;
    // FT-17: 本トランザクションの B+Tree インデックス論理 undo ログ。abort 時に
    // 索引エントリを逆適用 (Insert↔Delete) で巻き戻す。
    private readonly IndexUndoLog _indexUndoLog;
    private List<Action>? _onCommitted;
    private List<Action>? _onRolledBack;
    private TransactionState _state;

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
        AbortUndoHandler? undoHandler = null)
    {
        Id = id; Level = level; SnapshotLsn = snapshotLsn;
        _wal = wal;
        _nodeLocks = nodeLocks; _relLocks = relLocks; _indexLocks = indexLocks;
        _manager = manager;
        _adjStore = adjStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _undoHandler = undoHandler;
        _state = TransactionState.Active;
        _nodes = new TxNodeStore(nodeStore, nodeLocks, id);
        _relationships = new TxRelationshipStore(relStore, relLocks, id, _nodes);
        _properties = new TxPropertyStore(propStore);
        _indexes = new TxIndexManager(indexManager, indexLocks, id);
        _indexUndoLog = new IndexUndoLog(wal, id, indexManager);
        WalPageContext.Begin(wal, id);
        IndexUndoContext.Begin(_indexUndoLog);
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
            // FT-17: コミット時は索引変更を確定する。ambient コンテキストだけ破棄する。
            IndexUndoContext.End();
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
            try { RollBackIndexInPlace(); } catch { }
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
        RollBackIndexInPlace();
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

    // FT-17: replay this transaction's B+Tree index mutations in reverse
    // (Insert↔Delete). Clears the ambient IndexUndoContext first so the inverse
    // operations are not themselves recorded as new undo entries.
    private void RollBackIndexInPlace()
    {
        IndexUndoContext.End();
        _indexUndoLog.RollBack();
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

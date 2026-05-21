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
        IGraphAccessMethods? access = null)
    {
        Id = id; Level = level; SnapshotLsn = snapshotLsn;
        _wal = wal;
        _nodeLocks = nodeLocks; _relLocks = relLocks; _indexLocks = indexLocks;
        _manager = manager;
        _adjStore = adjStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _state = TransactionState.Active;
        _nodes = new TxNodeStore(nodeStore, nodeLocks, id);
        _relationships = new TxRelationshipStore(relStore, relLocks, id, _nodes);
        _properties = new TxPropertyStore(propStore);
        _indexes = new TxIndexManager(indexManager, indexLocks, id);
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
        _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty);
        WalPageContext.End();
        ReleaseAllLocks();
        _state = TransactionState.Aborted;
        _manager.OnAbort(Id);
        FireHooks(_onRolledBack);
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

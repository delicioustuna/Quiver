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
        // All PageImage WAL records are already logged by UnpinDirty calls.
        // Commit record comes last so recovery only replays images of committed txs.
        long lsn = _wal.Append(WalRecordType.Commit, Id, ReadOnlySpan<byte>.Empty);
        _wal.FlushTo(lsn);
        WalPageContext.End();
        ReleaseAllLocks();
        _state = TransactionState.Committed;
        _manager.OnCommit(Id);
    }

    public void Abort()
    {
        if (_state is TransactionState.Committed or TransactionState.Aborted) return;
        _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty);
        WalPageContext.End();
        ReleaseAllLocks();
        _state = TransactionState.Aborted;
        _manager.OnAbort(Id);
    }

    public void Dispose()
    {
        if (_state == TransactionState.Active) Abort();
    }

    private void ReleaseAllLocks()
    {
        _nodeLocks.ReleaseAll(Id);
        _relLocks.ReleaseAll(Id);
        _indexLocks.ReleaseAll(Id);
    }
}

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

    internal void OnCommit(TransactionId txId) => _active.TryRemove(txId.Value, out _);
    internal void OnAbort(TransactionId txId) => _active.TryRemove(txId.Value, out _);

    /// <summary>
    /// PW-14: swap the active adjacency store reference. Called by the backend
    /// after a compact rebuild — only safe while <see cref="ActiveCount"/> is 0
    /// since transactions snapshot the reference at <see cref="Begin"/>.
    /// </summary>
    internal void SwapAdjacencyStore(IAdjacencyBlockStore? next) => _adjStore = next;

    public void Dispose() { }
}

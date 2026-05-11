using Quiver.Core;
using Quiver.Index;

namespace Quiver.Transactions;

// Phase 1: index mutations are serialized via a global per-transaction lock.
internal sealed class TxIndexManager : IIndexManager
{
    private readonly IIndexManager _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;
    private const long GlobalIndexLockKey = long.MinValue;

    internal TxIndexManager(IIndexManager inner, LockManager locks, TransactionId txId)
    {
        _inner = inner; _locks = locks; _txId = txId;
    }

    public IBTreeIndex<int> CreateInt32Index(string name) { AcquireLock(); return _inner.CreateInt32Index(name); }
    public IBTreeIndex<long> CreateInt64Index(string name) { AcquireLock(); return _inner.CreateInt64Index(name); }
    public IBTreeIndex<double> CreateDoubleIndex(string name) { AcquireLock(); return _inner.CreateDoubleIndex(name); }
    public IBTreeIndex<string> CreateStringIndex(string name) { AcquireLock(); return _inner.CreateStringIndex(name); }
    public IBTreeIndex<byte[]> CreateBytesIndex(string name) { AcquireLock(); return _inner.CreateBytesIndex(name); }
    public bool DropIndex(string name) { AcquireLock(); return _inner.DropIndex(name); }
    public IEnumerable<string> ListIndexes() => _inner.ListIndexes();

    private void AcquireLock()
    {
        if (!_locks.TryAcquire(GlobalIndexLockKey, _txId))
            throw new TransactionException("Lock timeout acquiring index lock.");
    }
}

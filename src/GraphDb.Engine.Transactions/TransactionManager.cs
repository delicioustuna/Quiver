using GraphDb.Engine.Storage;
using GraphDb.Engine.Wal;

namespace GraphDb.Engine.Transactions;

internal sealed class TransactionManager : ITransactionManager
{
    private readonly IPageManager _pageManager;
    private readonly IWriteAheadLog _wal;
    private readonly LockManager _lockManager;

    public TransactionManager(IPageManager pageManager, IWriteAheadLog wal)
    {
        _pageManager = pageManager;
        _wal = wal;
        _lockManager = new LockManager();
    }

    public int ActiveCount => throw new NotImplementedException();
    public long OldestActiveLsn => throw new NotImplementedException();

    public ITransaction Begin(IsolationLevel level = IsolationLevel.SnapshotIsolation)
        => throw new NotImplementedException();

    public void Dispose() { }
}

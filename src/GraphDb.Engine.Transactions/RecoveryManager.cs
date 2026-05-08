using GraphDb.Engine.Storage;
using GraphDb.Engine.Wal;

namespace GraphDb.Engine.Transactions;

internal sealed class RecoveryManager : IRecoveryManager
{
    private readonly IPageManager _pageManager;
    private readonly IWriteAheadLog _wal;

    public RecoveryManager(IPageManager pageManager, IWriteAheadLog wal)
    {
        _pageManager = pageManager;
        _wal = wal;
    }

    public long Recover() => throw new NotImplementedException();
}

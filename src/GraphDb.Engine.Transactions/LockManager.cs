using System.Collections.Concurrent;
using GraphDb.Engine.Core;

namespace GraphDb.Engine.Transactions;

internal sealed class LockManager
{
    private readonly ConcurrentDictionary<long, LockHolder> _locks = new();

    public bool TryAcquire(long entityId, TransactionId txId) => throw new NotImplementedException();
    public void Release(long entityId, TransactionId txId) => throw new NotImplementedException();
    public void ReleaseAll(TransactionId txId) => throw new NotImplementedException();

    private sealed record LockHolder(TransactionId Owner, DateTime AcquiredAt);
}

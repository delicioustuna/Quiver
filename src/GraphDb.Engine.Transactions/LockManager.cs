using System.Collections.Concurrent;
using GraphDb.Engine.Core;

namespace GraphDb.Engine.Transactions;

internal sealed class LockManager
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<long, TransactionId> _locks = new();

    public bool TryAcquire(long entityId, TransactionId txId)
    {
        if (_locks.TryGetValue(entityId, out var existing) && existing == txId) return true;
        if (_locks.TryAdd(entityId, txId)) return true;

        var deadline = DateTime.UtcNow + LockTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!_locks.ContainsKey(entityId) && _locks.TryAdd(entityId, txId)) return true;
            if (_locks.TryGetValue(entityId, out existing) && existing == txId) return true;
            Thread.SpinWait(1000);
        }
        return false;
    }

    public void Release(long entityId, TransactionId txId)
        => _locks.TryRemove(new KeyValuePair<long, TransactionId>(entityId, txId));

    public void ReleaseAll(TransactionId txId)
    {
        foreach (var kvp in _locks)
            if (kvp.Value == txId)
                _locks.TryRemove(kvp.Key, out _);
    }
}

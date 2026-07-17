using System.Collections.Concurrent;

namespace Quiver.Core;

/// <summary>
/// コミット済み高水位と中止済みgapを管理します。
/// </summary>
internal sealed class CommittedTxRegistry
{
    private readonly ConcurrentDictionary<long, byte> _committed = new();
    private readonly ConcurrentDictionary<long, byte> _aborted = new();
    private long _committedHighWater;
    private long _maxObservedTxId;
    private long _recoveryHorizon;

    internal CommittedTxRegistry()
    {
        _committed[TransactionId.Bootstrap.Value] = 0;
        _committedHighWater = TransactionId.Bootstrap.Value;
        _maxObservedTxId = TransactionId.Bootstrap.Value;
        _recoveryHorizon = TransactionId.Bootstrap.Value;
    }

    internal long RecoveryHorizon
    {
        get => Volatile.Read(ref _recoveryHorizon);
        set
        {
            Volatile.Write(ref _recoveryHorizon, value);
            AdvanceHighWater(value);
        }
    }

    internal long MaxObservedTxId => Volatile.Read(ref _maxObservedTxId);

    internal long CommittedHighWater => Volatile.Read(ref _committedHighWater);

    internal void RecordMaxObservedTxId(long observed)
    {
        long current = Volatile.Read(ref _maxObservedTxId);
        while (observed > current)
        {
            long previous = Interlocked.CompareExchange(ref _maxObservedTxId, observed, current);
            if (previous == current) return;
            current = previous;
        }
    }

    internal void MarkCommitted(TransactionId txId)
    {
        _committed[txId.Value] = 0;
        _aborted.TryRemove(txId.Value, out _);
        RecordMaxObservedTxId(txId.Value);
        AdvanceHighWater(txId.Value);
    }

    internal void MarkAborted(TransactionId txId)
    {
        if (txId.Value <= TransactionId.Bootstrap.Value) return;
        _aborted[txId.Value] = 0;
        _committed.TryRemove(txId.Value, out _);
        RecordMaxObservedTxId(txId.Value);
    }

    internal SnapshotState Capture(TransactionId? activeWriterId)
    {
        long highWater = CommittedHighWater;
        var gaps = _aborted.Keys
            .Where(id => id <= highWater)
            .ToHashSet();
        return new SnapshotState(highWater, gaps, activeWriterId);
    }

    internal bool IsCommitted(long txId)
    {
        if (txId <= Volatile.Read(ref _recoveryHorizon)) return true;
        return _committed.ContainsKey(txId);
    }

    internal int Count => _committed.Count;

    internal int PruneBelow(long horizonTxId)
    {
        int removed = 0;
        foreach (long key in _committed.Keys)
        {
            if (key == TransactionId.Bootstrap.Value || key >= horizonTxId) continue;
            if (_committed.TryRemove(key, out _)) removed++;
        }

        foreach (long key in _aborted.Keys)
        {
            if (key < horizonTxId)
                _aborted.TryRemove(key, out _);
        }

        return removed;
    }

    private void AdvanceHighWater(long value)
    {
        long current = Volatile.Read(ref _committedHighWater);
        while (value > current)
        {
            long previous = Interlocked.CompareExchange(ref _committedHighWater, value, current);
            if (previous == current) return;
            current = previous;
        }
    }
}

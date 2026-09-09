using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Yatagarasu.Core;

/// <summary>
/// コミット済み高水位と中止済みgapを管理します。
/// </summary>
internal sealed class CommittedTxRegistry
{
    private readonly ConcurrentDictionary<long, byte> _committed = new();
    private readonly ConcurrentDictionary<long, byte> _aborted = new();
    private long _committedHighWater;
    private long _maxObservedTxId;
    private long _compactedVisibilityHorizon;
    private readonly Lock _publicationGate = new();
    private TransactionId? _activeWriterId;
    private PublishedSnapshot _published = new(SnapshotState.Empty);

    internal sealed record PublishedSnapshot(SnapshotState Snapshot);
    internal PublishedSnapshot Published => Volatile.Read(ref _published);
    internal Action? BeforePublishForTest { get; set; }

    internal CommittedTxRegistry()
    {
        _committed[TransactionId.Bootstrap.Value] = 0;
        _committedHighWater = TransactionId.Bootstrap.Value;
        _maxObservedTxId = TransactionId.Bootstrap.Value;
        _compactedVisibilityHorizon = TransactionId.Bootstrap.Value;
    }

    internal long CompactedVisibilityHorizon
    {
        get => Volatile.Read(ref _compactedVisibilityHorizon);
        set
        {
            lock (_publicationGate)
            {
                Volatile.Write(ref _compactedVisibilityHorizon, value);
                AdvanceHighWater(value);
                PublishSnapshot();
            }
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
        lock (_publicationGate)
        {
            _committed[txId.Value] = 0;
            _aborted.TryRemove(txId.Value, out _);
            RecordMaxObservedTxId(txId.Value);
            AdvanceHighWater(txId.Value);
            if (_activeWriterId == txId) _activeWriterId = null;
            PublishSnapshot();
        }
    }

    internal void MarkAborted(TransactionId txId)
    {
        if (txId.Value <= TransactionId.Bootstrap.Value) return;
        lock (_publicationGate)
        {
            _aborted[txId.Value] = 0;
            _committed.TryRemove(txId.Value, out _);
            RecordMaxObservedTxId(txId.Value);
            PublishSnapshot();
        }
    }

    internal void SetActiveWriter(TransactionId? transactionId)
    {
        lock (_publicationGate)
        {
            _activeWriterId = transactionId;
            PublishSnapshot();
        }
    }

    // 更新はデータベースの書き込み権限で直列化し、復旧はデータベースを開く際に直列に実行する。
    // このロックは組の構築と公開だけを保護し、読み取り側は取得しない。
    private void PublishSnapshot()
    {
        System.Diagnostics.Debug.Assert(_publicationGate.IsHeldByCurrentThread);
        long highWater = CommittedHighWater;
        IReadOnlySet<long> gaps = _aborted.IsEmpty ? FrozenSet<long>.Empty
            : _aborted.Keys.Where(id => id <= highWater).ToFrozenSet();
        BeforePublishForTest?.Invoke();
        Volatile.Write(ref _published,
            new PublishedSnapshot(new SnapshotState(highWater, gaps, _activeWriterId)));
    }

    internal void RestoreRecoveredTransactions(
        IEnumerable<long> winners, IEnumerable<long> observed, long maxObserved)
    {
        lock (_publicationGate)
        {
            var committedIds = winners.ToHashSet();
            foreach (long id in committedIds)
            {
                _committed[id] = 0;
                _aborted.TryRemove(id, out _);
                AdvanceHighWater(id);
            }
            foreach (long id in observed)
            {
                if (id > TransactionId.Bootstrap.Value && !committedIds.Contains(id))
                {
                    _aborted[id] = 0;
                    _committed.TryRemove(id, out _);
                }
            }
            RecordMaxObservedTxId(maxObserved);
            PublishSnapshot();
        }
    }

    internal bool IsCommitted(long txId)
    {
        // checkpoint/vacuum がこの horizon を進める前に、中止 tx の物理効果を
        // rollback/reclaim 済みにする。gap を捨てた後も残存 version が中止 tx を参照しない。
        if (txId <= Volatile.Read(ref _compactedVisibilityHorizon)) return true;
        return _committed.ContainsKey(txId);
    }

    internal void RestoreCheckpointedHighWater(long highWater)
    {
        if (highWater <= TransactionId.Bootstrap.Value)
            return;
        CompactedVisibilityHorizon = highWater;
        RecordMaxObservedTxId(highWater);
    }

    internal int Count => _committed.Count;

    internal int PruneBelow(long horizonTxId, bool dryRun = false)
    {
        lock (_publicationGate)
        {
            int removed = 0;
            foreach (long key in _committed.Keys)
            {
                if (key == TransactionId.Bootstrap.Value || key >= horizonTxId) continue;
                if (dryRun || _committed.TryRemove(key, out _)) removed++;
            }

            if (dryRun) return removed;
            foreach (long key in _aborted.Keys)
            {
                if (key < horizonTxId)
                    _aborted.TryRemove(key, out _);
            }

            PublishSnapshot();
            return removed;
        }
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

using System.Diagnostics;
using Quiver.Core;
using Quiver.Core.Telemetry;

namespace Quiver.Transactions;

/// <summary>
/// FT-24: shared/exclusive ロックマネージャ + FIFO wait queue。
/// </summary>
/// <remarks>
/// データ構造: entityId → <see cref="LockEntry"/>。各エントリは exclusive owner (高々 1)、
/// shared owner 集合、待機キューを持つ。同一 tx 内の再入: exclusive 保有中は any mode、
/// shared 保有中の単独 reader は exclusive へ無待機昇格、他 reader 居れば wait (FT-25 で
/// 検出予定の deadlock 経路)。待機キューは FIFO で、head の連続する shared 待機者は同時に
/// 起こす (shared バースト)。タイムアウトは <see cref="ITransaction"/> 経由で渡される。
/// </remarks>
internal sealed class LockManager
{
    private readonly Dictionary<long, LockEntry> _locks = new();
    // Monitor.Wait に渡す関係で object 型 (System.Threading.Lock は変換時に警告 CS9216)。
    private readonly object _gate = new();

    /// <summary>後方互換 overload: Exclusive + 5s タイムアウトを既定とする。</summary>
    public bool TryAcquire(long entityId, TransactionId txId)
        => TryAcquire(entityId, txId, LockMode.Exclusive, TimeSpan.FromSeconds(5));

    public bool TryAcquire(long entityId, TransactionId txId, LockMode mode, TimeSpan timeout)
    {
        DateTime deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTime.MaxValue
            : DateTime.UtcNow + timeout;
        // OB-1: lock 取得の wait 時間を測る。即時取得時は near-zero。
        var sw = Stopwatch.StartNew();
        try
        {

        lock (_gate)
        {
            while (true)
            {
                if (!_locks.TryGetValue(entityId, out var entry))
                {
                    entry = new LockEntry();
                    _locks[entityId] = entry;
                }

                // Same-tx 再入。
                if (entry.ExclusiveOwner is { } owner && owner == txId)
                    return true;
                bool alreadyShared = entry.SharedOwners?.Contains(txId) == true;
                if (alreadyShared && mode == LockMode.Shared)
                    return true;
                if (alreadyShared && mode == LockMode.Exclusive)
                {
                    // 単独 reader なら無待機昇格、他 reader 居れば待機。
                    if (entry.SharedOwners!.Count == 1 && entry.ExclusiveOwner is null)
                    {
                        entry.SharedOwners.Remove(txId);
                        entry.ExclusiveOwner = txId;
                        return true;
                    }
                    // 昇格待ち: waiter として並ぶが、現在保有している shared はリリースしない
                    // (典型的な S→X upgrade。他 reader が release するのを待つ)。
                    if (!Wait(entry, txId, mode, deadline))
                        return false;
                    continue;
                }

                // 即時取得可能か?
                if (CanGrant(entry, mode))
                {
                    Grant(entry, txId, mode);
                    return true;
                }

                // wait queue に並ぶ。barrier: 既に waiter が居れば FIFO を維持するため新規も並ぶ
                // (writer starvation 防止)。
                if (!Wait(entry, txId, mode, deadline))
                    return false;
                // 起こされた → ループ先頭で再評価。
            }
        }
        }
        finally
        {
            QuiverTelemetry.LockWaitMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private static bool CanGrant(LockEntry entry, LockMode mode)
    {
        if (entry.ExclusiveOwner is not null) return false;
        if (mode == LockMode.Exclusive)
            return (entry.SharedOwners?.Count ?? 0) == 0 && entry.Waiters.Count == 0;
        // Shared: head waiter が exclusive のときは FIFO のため待つ (writer starvation 回避)。
        if (entry.Waiters.Count > 0 && entry.Waiters.Peek().Mode == LockMode.Exclusive)
            return false;
        return true;
    }

    private static void Grant(LockEntry entry, TransactionId txId, LockMode mode)
    {
        if (mode == LockMode.Exclusive)
            entry.ExclusiveOwner = txId;
        else
            (entry.SharedOwners ??= new HashSet<TransactionId>()).Add(txId);
    }

    private bool Wait(LockEntry entry, TransactionId txId, LockMode mode, DateTime deadline)
    {
        var waiter = new Waiter(txId, mode);
        entry.Waiters.Enqueue(waiter);
        try
        {
            while (!waiter.Granted)
            {
                // FT-25: DeadlockDetector が当該 waiter を犠牲者として印付けたら DeadlockException を投げて
                // 上位 (TxNodeStore.Acquire 等の wrapping ではなく user 経路) へ伝播させる。
                if (waiter.DeadlockAborted)
                    throw new DeadlockException(txId,
                        $"Transaction {txId.Value} aborted by deadlock detector while waiting for {(mode == LockMode.Exclusive ? "exclusive" : "shared")} lock.");

                int remaining;
                if (deadline == DateTime.MaxValue)
                    remaining = Timeout.Infinite;
                else
                {
                    var span = deadline - DateTime.UtcNow;
                    if (span <= TimeSpan.Zero) return false;
                    remaining = (int)Math.Min(int.MaxValue, span.TotalMilliseconds);
                }
                // Monitor.Wait は _gate を一時解放してシグナルを待つ。
                if (!System.Threading.Monitor.Wait(_gate, remaining))
                {
                    // タイムアウト復帰直前に detector が印付けていれば deadlock として扱う。
                    if (waiter.DeadlockAborted)
                        throw new DeadlockException(txId,
                            $"Transaction {txId.Value} aborted by deadlock detector while waiting for {(mode == LockMode.Exclusive ? "exclusive" : "shared")} lock.");
                    return false;
                }
            }
            return true;
        }
        finally
        {
            if (!waiter.Granted)
            {
                // タイムアウト / 中断 / 例外時に自身をキューから除去。
                RemoveWaiter(entry, waiter);
            }
        }
    }

    /// <summary>
    /// FT-25: wait-for edges (waiter → holder) を <paramref name="edges"/> に追記する。
    /// self-edge (例: S→X upgrade 待機で自身が shared owner) は除外する。
    /// </summary>
    internal void SnapshotWaitEdges(List<(TransactionId Waiter, TransactionId Holder)> edges)
    {
        lock (_gate)
        {
            foreach (var entry in _locks.Values)
            {
                if (entry.Waiters.Count == 0) continue;
                foreach (var w in entry.Waiters)
                {
                    if (entry.ExclusiveOwner is { } xo && xo != w.TxId)
                        edges.Add((w.TxId, xo));
                    if (entry.SharedOwners != null)
                    {
                        foreach (var so in entry.SharedOwners)
                        {
                            if (so != w.TxId) edges.Add((w.TxId, so));
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// FT-25: <paramref name="victim"/> の waiter を印付けて起こす。見つからなければ false。
    /// 同じ tx が複数 lock entry で待っている可能性は無い (1 tx は同時に 1 つの Wait しか持たない)。
    /// </summary>
    internal bool TryAbortWaiter(TransactionId victim)
    {
        lock (_gate)
        {
            foreach (var entry in _locks.Values)
            {
                foreach (var w in entry.Waiters)
                {
                    if (w.TxId == victim && !w.Granted && !w.DeadlockAborted)
                    {
                        w.DeadlockAborted = true;
                        System.Threading.Monitor.PulseAll(_gate);
                        return true;
                    }
                }
            }
            return false;
        }
    }

    private static void RemoveWaiter(LockEntry entry, Waiter target)
    {
        if (entry.Waiters.Count == 0) return;
        int count = entry.Waiters.Count;
        for (int i = 0; i < count; i++)
        {
            var w = entry.Waiters.Dequeue();
            if (w != target) entry.Waiters.Enqueue(w);
        }
    }

    public void Release(long entityId, TransactionId txId)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(entityId, out var entry)) return;
            ReleaseFrom(entry, txId);
            if (IsEmpty(entry)) _locks.Remove(entityId);
            else WakeWaiters(entry);
        }
    }

    public void ReleaseAll(TransactionId txId)
    {
        lock (_gate)
        {
            List<long>? toRemove = null;
            foreach (var kvp in _locks)
            {
                var entry = kvp.Value;
                bool changed = ReleaseFrom(entry, txId);
                if (!changed) continue;
                if (IsEmpty(entry))
                    (toRemove ??= new List<long>()).Add(kvp.Key);
                else
                    WakeWaiters(entry);
            }
            if (toRemove != null)
                foreach (var id in toRemove) _locks.Remove(id);
        }
    }

    private static bool ReleaseFrom(LockEntry entry, TransactionId txId)
    {
        bool changed = false;
        if (entry.ExclusiveOwner is { } owner && owner == txId)
        {
            entry.ExclusiveOwner = null;
            changed = true;
        }
        if (entry.SharedOwners?.Remove(txId) == true)
            changed = true;
        return changed;
    }

    private static bool IsEmpty(LockEntry entry)
        => entry.ExclusiveOwner is null
           && (entry.SharedOwners is null || entry.SharedOwners.Count == 0)
           && entry.Waiters.Count == 0;

    private void WakeWaiters(LockEntry entry)
    {
        // FIFO に従い、起こせる waiter を順に grant し、それ以上 grant できなくなったら止める。
        // 連続する shared waiter はバーストで grant する (shared 同士は競合しないため)。
        while (entry.Waiters.Count > 0)
        {
            var head = entry.Waiters.Peek();
            // S→X upgrade 待機の特例: head が exclusive 待ちで、shared owner が自分 1 人だけなら起こす。
            if (head.Mode == LockMode.Exclusive
                && entry.ExclusiveOwner is null
                && (entry.SharedOwners?.Count ?? 0) == 0)
            {
                entry.Waiters.Dequeue();
                entry.ExclusiveOwner = head.TxId;
                head.Granted = true;
                System.Threading.Monitor.PulseAll(_gate);
                return; // exclusive を 1 つ起こしたら他は wait
            }
            if (head.Mode == LockMode.Exclusive
                && entry.ExclusiveOwner is null
                && entry.SharedOwners?.Count == 1
                && entry.SharedOwners.Contains(head.TxId))
            {
                // 単独 reader 自身が exclusive 待機している = 昇格。
                entry.Waiters.Dequeue();
                entry.SharedOwners.Remove(head.TxId);
                entry.ExclusiveOwner = head.TxId;
                head.Granted = true;
                System.Threading.Monitor.PulseAll(_gate);
                return;
            }
            if (head.Mode == LockMode.Shared
                && entry.ExclusiveOwner is null)
            {
                // 連続する shared を全部起こす。
                while (entry.Waiters.Count > 0 && entry.Waiters.Peek().Mode == LockMode.Shared)
                {
                    var w = entry.Waiters.Dequeue();
                    (entry.SharedOwners ??= new HashSet<TransactionId>()).Add(w.TxId);
                    w.Granted = true;
                }
                System.Threading.Monitor.PulseAll(_gate);
                return;
            }
            // それ以外は起こせない。
            return;
        }
    }

    // 診断用 (テスト)。
    internal int ActiveLockCount
    {
        get { lock (_gate) return _locks.Count; }
    }

    private sealed class LockEntry
    {
        public TransactionId? ExclusiveOwner;
        public HashSet<TransactionId>? SharedOwners;
        public readonly Queue<Waiter> Waiters = new();
    }

    private sealed class Waiter(TransactionId txId, LockMode mode)
    {
        public readonly TransactionId TxId = txId;
        public readonly LockMode Mode = mode;
        public bool Granted;
        // FT-25: DeadlockDetector が犠牲者として印付けると true。Wait は次回 wake で例外を投げる。
        public bool DeadlockAborted;
    }
}

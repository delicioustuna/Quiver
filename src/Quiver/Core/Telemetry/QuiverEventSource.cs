using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace Quiver.Telemetry;

/// <summary>
/// OB-2: <c>dotnet-counters monitor -n &lt;pid&gt; --counters Quiver-EventSource</c> で
/// バッファプール / WAL / トランザクション / ロック / 索引 / vacuum の主要メトリクスを
/// in-box (追加 NuGet 不要) でリアルタイム観測するための <see cref="EventSource"/>。
/// </summary>
/// <remarks>
/// 設計指針:
/// <list type="bullet">
///   <item>
///     <para>
///     <b>process-wide シングルトン</b>。<see cref="Log"/> から各 hot path (PagedFile,
///     WriteAheadLog, Transaction, LockManager, DeadlockDetector, RecoveryManager,
///     Vacuum) が直接インクリメント API を呼ぶ。複数 <c>GraphDatabase</c> インスタンスが
///     同一プロセスに存在しても累計 / レート系メトリクスは合算される。
///     </para>
///   </item>
///   <item>
///     <para>
///     <b>gauge 系</b> (ActiveTxCount / CheckpointThresholdBytes / BufferPoolSizeBytes)
///     はインスタンスごとに値を持つため、<see cref="RegisterActiveTxCountProvider"/> 等で
///     <see cref="Func{TResult}"/> を登録し、PollingCounter が呼び出し時に全 provider の
///     合計を返す。<see cref="IDisposable"/> を Dispose() するとプロバイダ解除。
///     </para>
///   </item>
///   <item>
///     <para>
///     <b>カウンタ生成タイミング</b>: <see cref="OnEventCommand"/> で
///     <see cref="EventCommand.Enable"/> 受信時に lazily 生成する。EventSource が
///     未有効化なら PollingCounter 自体が生成されないので overhead 0。
///     </para>
///   </item>
///   <item>
///     <para>
///     <b>計装オーバヘッド</b>: hot path から呼ぶのは <see cref="Interlocked.Increment(ref long)"/>
///     等のアトミック操作のみ (PollingCounter のコールバックは EventSource 側スレッドが 1Hz で
///     polling し、hot path をブロックしない)。
///     </para>
///   </item>
/// </list>
/// </remarks>
[EventSource(Name = "Quiver-EventSource")]
public sealed class QuiverEventSource : EventSource
{
    /// <summary>プロセス全体の singleton。各 hot path はここから直接インクリメント API を呼ぶ。</summary>
    public static readonly QuiverEventSource Log = new();

    // -------------------- process-wide atomic counters --------------------

    // BufferPool
    private long _bufferPoolHits;
    private long _bufferPoolMisses;
    private long _bufferPoolEvictions;

    // WAL
    private long _walBytesWritten;
    private long _walPendingFlushRequests;

    // Transaction
    private long _txCommitCount;
    private long _txAbortCount;
    private long _deadlockVictimCount;
    private long _crashRecoveryCount;

    // Lock
    private long _lockWaitTotalMs;
    private long _lockWaitSampleCount;
    private long _lockContentionCount;

    // Index orphan: 最後に CheckIndexConsistency が観測した orphan 件数 (進行中の vacuum 等で更新)。
    private long _indexOrphanLastObserved;

    // Vacuum: 0..100, vacuum 非実行時は 0。
    private long _vacuumProgressPercent;

    // -------------------- per-instance gauge providers --------------------

    private readonly ConcurrentDictionary<object, Func<long>> _activeTxCountProviders = new();
    private readonly ConcurrentDictionary<object, Func<long>> _checkpointThresholdProviders = new();
    private readonly ConcurrentDictionary<object, Func<long>> _bufferPoolSizeBytesProviders = new();

    // -------------------- lazy counter handles --------------------

    private PollingCounter? _bufferPoolHitRatioCounter;
    private PollingCounter? _bufferPoolEvictionsCounter;
    private PollingCounter? _bufferPoolSizeBytesCounter;
    private PollingCounter? _walPendingFlushCounter;
    private PollingCounter? _checkpointThresholdCounter;
    private PollingCounter? _activeTxCountCounter;
    private PollingCounter? _lockWaitAvgMsCounter;
    private PollingCounter? _lockContentionCounter;
    private PollingCounter? _indexOrphanCountCounter;
    private PollingCounter? _vacuumProgressCounter;

    private IncrementingPollingCounter? _walBytesPerSecCounter;
    private IncrementingPollingCounter? _txCommitPerSecCounter;
    private IncrementingPollingCounter? _txAbortPerSecCounter;
    private IncrementingPollingCounter? _deadlockVictimRateCounter;
    private IncrementingPollingCounter? _crashRecoveryRateCounter;

    private QuiverEventSource() { }

    /// <inheritdoc/>
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            EnsureCountersInitialized();
        }
    }

    private void EnsureCountersInitialized()
    {
        // ------- gauges -------
        _bufferPoolHitRatioCounter ??= new PollingCounter(
            "buffer-pool-hit-ratio", this, GetBufferPoolHitRatio)
        {
            DisplayName = "Buffer-pool hit ratio",
            DisplayUnits = "ratio",
        };
        _bufferPoolEvictionsCounter ??= new PollingCounter(
            "buffer-pool-evictions", this, () => Volatile.Read(ref _bufferPoolEvictions))
        {
            DisplayName = "Buffer-pool evictions (running total)",
            DisplayUnits = "evictions",
        };
        _bufferPoolSizeBytesCounter ??= new PollingCounter(
            "buffer-pool-size-bytes", this, () => Sum(_bufferPoolSizeBytesProviders))
        {
            DisplayName = "Buffer-pool size",
            DisplayUnits = "bytes",
        };
        _walPendingFlushCounter ??= new PollingCounter(
            "wal-pending-flush-count", this, () => Volatile.Read(ref _walPendingFlushRequests))
        {
            DisplayName = "Pending WAL flush requests",
        };
        _checkpointThresholdCounter ??= new PollingCounter(
            "current-checkpoint-threshold-bytes", this, () => Sum(_checkpointThresholdProviders))
        {
            DisplayName = "Current checkpoint threshold (Adaptive 時は controller 値)",
            DisplayUnits = "bytes",
        };
        _activeTxCountCounter ??= new PollingCounter(
            "active-tx-count", this, () => Sum(_activeTxCountProviders))
        {
            DisplayName = "Active transactions",
        };
        _lockWaitAvgMsCounter ??= new PollingCounter(
            "lock-wait-avg-ms", this, GetLockWaitAvgMs)
        {
            DisplayName = "Average lock-wait time",
            DisplayUnits = "ms",
        };
        _lockContentionCounter ??= new PollingCounter(
            "lock-contention-count", this, () => Volatile.Read(ref _lockContentionCount))
        {
            DisplayName = "Lock contention count (running total)",
        };
        _indexOrphanCountCounter ??= new PollingCounter(
            "index-orphan-count", this, () => Volatile.Read(ref _indexOrphanLastObserved))
        {
            DisplayName = "Index orphan count (CheckIndexConsistency 最終観測値)",
        };
        _vacuumProgressCounter ??= new PollingCounter(
            "vacuum-progress-percent", this, () => Volatile.Read(ref _vacuumProgressPercent))
        {
            DisplayName = "Vacuum progress",
            DisplayUnits = "percent",
        };

        // ------- rate counters (cumulative → per-sec) -------
        _walBytesPerSecCounter ??= new IncrementingPollingCounter(
            "wal-bytes-per-sec", this, () => Volatile.Read(ref _walBytesWritten))
        {
            DisplayName = "WAL bytes written",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
            DisplayUnits = "bytes",
        };
        _txCommitPerSecCounter ??= new IncrementingPollingCounter(
            "tx-commit-per-sec", this, () => Volatile.Read(ref _txCommitCount))
        {
            DisplayName = "Transaction commits / s",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _txAbortPerSecCounter ??= new IncrementingPollingCounter(
            "tx-abort-per-sec", this, () => Volatile.Read(ref _txAbortCount))
        {
            DisplayName = "Transaction aborts / s",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _deadlockVictimRateCounter ??= new IncrementingPollingCounter(
            "tx-deadlock-victim-count", this, () => Volatile.Read(ref _deadlockVictimCount))
        {
            DisplayName = "Deadlock victims / s",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _crashRecoveryRateCounter ??= new IncrementingPollingCounter(
            "crash-recovery-count", this, () => Volatile.Read(ref _crashRecoveryCount))
        {
            DisplayName = "Crash recoveries / s",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
    }

    // ============================================================================
    // Hot-path increment API (各サブシステムから直接呼ばれる)
    // ============================================================================

    /// <summary>BufferPool フレームヒット (1 回 = 1)。</summary>
    public void BufferPoolHit() => Interlocked.Increment(ref _bufferPoolHits);

    /// <summary>BufferPool フレームミス (1 回 = 1)。</summary>
    public void BufferPoolMiss() => Interlocked.Increment(ref _bufferPoolMisses);

    /// <summary>BufferPool eviction (1 回 = 1)。フレーム入れ替えで dirty が書き出された場合に呼ぶ。</summary>
    public void BufferPoolEviction() => Interlocked.Increment(ref _bufferPoolEvictions);

    /// <summary>WAL に書いたバイト数を加算する。</summary>
    public void WalBytesWritten(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _walBytesWritten, bytes);
    }

    /// <summary>WAL flush 要求の保留件数を 1 増やす (<see cref="WalFlushRequestCompleted"/> と対で呼ぶ)。</summary>
    public void WalFlushRequestStarted() => Interlocked.Increment(ref _walPendingFlushRequests);

    /// <summary>WAL flush 要求の保留件数を 1 減らす。</summary>
    public void WalFlushRequestCompleted() => Interlocked.Decrement(ref _walPendingFlushRequests);

    /// <summary>トランザクション commit 完了 (1 回 = 1)。</summary>
    public void TxCommit() => Interlocked.Increment(ref _txCommitCount);

    /// <summary>トランザクション abort 完了 (1 回 = 1)。</summary>
    public void TxAbort() => Interlocked.Increment(ref _txAbortCount);

    /// <summary>デッドロック検出器が犠牲者を中断 (1 回 = 1)。</summary>
    public void DeadlockVictim() => Interlocked.Increment(ref _deadlockVictimCount);

    /// <summary>crash recovery が起動した (1 回 = 1)。<see cref="Quiver.Transactions.RecoveryManager.Recover"/> で 1 度呼ぶ。</summary>
    public void CrashRecovery() => Interlocked.Increment(ref _crashRecoveryCount);

    /// <summary>
    /// ロック取得待ち時間 (ms) と「待ちが発生したかどうか」を記録する。
    /// <paramref name="contended"/> = true なら同時にロック競合カウンタも 1 増やす。
    /// </summary>
    public void RecordLockWait(double waitMs, bool contended)
    {
        long rounded = waitMs <= 0 ? 0 : (long)waitMs;
        Interlocked.Add(ref _lockWaitTotalMs, rounded);
        Interlocked.Increment(ref _lockWaitSampleCount);
        if (contended) Interlocked.Increment(ref _lockContentionCount);
    }

    /// <summary>
    /// CheckIndexConsistency が観測した orphan 件数を gauge にセットする。
    /// 値は次回 CheckIndexConsistency 呼び出しまで保持される。
    /// </summary>
    public void SetIndexOrphanCount(long count)
        => Volatile.Write(ref _indexOrphanLastObserved, count);

    /// <summary>Vacuum 進捗 (0..100) を gauge にセットする。vacuum 完了時は 0 に戻すこと。</summary>
    public void SetVacuumProgress(long percent)
    {
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        Volatile.Write(ref _vacuumProgressPercent, percent);
    }

    // ============================================================================
    // Per-instance gauge provider 登録
    // ============================================================================

    /// <summary>active な transaction 数を返す provider を登録する。Dispose で解除。</summary>
    public IDisposable RegisterActiveTxCountProvider(Func<long> provider)
        => Register(_activeTxCountProviders, provider);

    /// <summary>現在の checkpoint threshold (bytes) を返す provider を登録する。Dispose で解除。</summary>
    public IDisposable RegisterCheckpointThresholdProvider(Func<long> provider)
        => Register(_checkpointThresholdProviders, provider);

    /// <summary>buffer pool が確保しているメモリ量 (bytes) を返す provider を登録する。Dispose で解除。</summary>
    public IDisposable RegisterBufferPoolSizeBytesProvider(Func<long> provider)
        => Register(_bufferPoolSizeBytesProviders, provider);

    // ============================================================================
    // Helpers
    // ============================================================================

    private double GetBufferPoolHitRatio()
    {
        long h = Volatile.Read(ref _bufferPoolHits);
        long m = Volatile.Read(ref _bufferPoolMisses);
        long total = h + m;
        return total == 0 ? 0.0 : (double)h / total;
    }

    private double GetLockWaitAvgMs()
    {
        long total = Volatile.Read(ref _lockWaitTotalMs);
        long n = Volatile.Read(ref _lockWaitSampleCount);
        return n == 0 ? 0.0 : (double)total / n;
    }

    private static long Sum(ConcurrentDictionary<object, Func<long>> providers)
    {
        long sum = 0;
        foreach (var p in providers.Values)
        {
            try { sum += p(); } catch { /* provider 失敗で polling 全体を止めない */ }
        }
        return sum;
    }

    private static IDisposable Register(ConcurrentDictionary<object, Func<long>> bag, Func<long> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var key = new object();
        bag[key] = provider;
        return new Registration(bag, key);
    }

    private sealed class Registration(ConcurrentDictionary<object, Func<long>> bag, object key) : IDisposable
    {
        private ConcurrentDictionary<object, Func<long>>? _bag = bag;
        private readonly object _key = key;

        public void Dispose()
        {
            var b = Interlocked.Exchange(ref _bag, null);
            b?.TryRemove(_key, out _);
        }
    }
}

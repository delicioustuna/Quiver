using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace Quiver.Telemetry;

/// <summary>
/// <c>dotnet-counters monitor -n &lt;pid&gt; --counters Quiver-EventSource</c> で
/// バッファプール / WAL / トランザクション / writer / snapshot / maintenance の主要メトリクスを
/// in-box (追加 NuGet 不要) でリアルタイム観測するための <see cref="EventSource"/>。
/// </summary>
/// <remarks>
/// 設計指針:
/// <list type="bullet">
///  <item>
///    <para>
///    <b>process-wide シングルトン</b>。<see cref="Log"/> から各 hot path (PagedFile,
///    WriteAheadLog, Transaction, RecoveryManager,
///    Vacuum) が直接インクリメント API を呼ぶ。複数 <c>QuiverDatabase</c> インスタンスが
///    同一プロセスに存在しても累計 / レート系メトリクスは合算される。
///    </para>
///  </item>
///  <item>
///    <para>
///    <b>gauge 系</b> (ActiveTxCount / CheckpointThresholdBytes / BufferPoolSizeBytes)
///    はインスタンスごとに値を持つため、<see cref="RegisterActiveTxCountProvider"/> 等で
///    <see cref="Func{TResult}"/> を登録し、PollingCounter が呼び出し時に全 provider の
///    合計を返す。<see cref="IDisposable"/> を Dispose() するとプロバイダ解除。
///    </para>
///  </item>
///  <item>
///    <para>
///    <b>カウンタ生成タイミング</b>: <see cref="OnEventCommand"/> で
///    <see cref="EventCommand.Enable"/> 受信時に lazily 生成する。EventSource が
///    未有効化なら PollingCounter 自体が生成されないので overhead 0。
///    </para>
///  </item>
///  <item>
///    <para>
///    <b>計装オーバヘッド</b>: hot path から呼ぶのは <see cref="Interlocked.Increment(ref long)"/>
///    等のアトミック操作のみ (PollingCounter のコールバックは EventSource 側スレッドが 1Hz で
///    polling し、hot path をブロックしない)。
///    </para>
///  </item>
/// </list>
/// </remarks>
[EventSource(Name = "Quiver-EventSource")]
internal sealed class QuiverEventSource : EventSource
{
    internal const string EventSourceName = "Quiver-EventSource";

    /// <summary>プロセス全体の singleton。各 hot path はここから直接インクリメント API を呼ぶ。</summary>
    public static readonly QuiverEventSource Log = new();

    // -------------------- プロセス全体の atomic counter --------------------

    // BufferPool
    private long _bufferPoolHits;
    private long _bufferPoolMisses;
    private long _bufferPoolEvictions;

    // WAL
    private long _walBytesWritten;
    private long _walPendingFlushRequests;

    // トランザクション
    private long _txCommitCount;
    private long _txAbortCount;
    private long _crashRecoveryCount;
    private long _writerWaitDurationMicroseconds;
    private long _writerContentionCount;
    private long _rebuildActive;
    private long _garbageCollectionActive;

    // Index orphan: 最後に CheckIndexConsistency が観測した orphan 件数 (進行中の vacuum 等で更新)。
    private long _indexOrphanLastObserved;

    // Vacuum: 0..100, vacuum 非実行時は 0。
    private long _vacuumProgressPercent;

    // -------------------- インスタンス単位の gauge provider --------------------

    private readonly ConcurrentDictionary<object, Func<long>> _activeTxCountProviders = new();
    private readonly ConcurrentDictionary<object, Func<long>> _checkpointThresholdProviders = new();
    private readonly ConcurrentDictionary<object, Func<long>> _bufferPoolSizeBytesProviders = new();
    private readonly ConcurrentDictionary<object, SnapshotProvider> _snapshotProviders = new();

    // -------------------- 遅延生成する counter handle --------------------

    private PollingCounter? _bufferPoolHitRatioCounter;
    private PollingCounter? _bufferPoolEvictionsCounter;
    private PollingCounter? _bufferPoolSizeBytesCounter;
    private PollingCounter? _walPendingFlushCounter;
    private PollingCounter? _checkpointThresholdCounter;
    private PollingCounter? _activeTxCountCounter;
    private PollingCounter? _indexOrphanCountCounter;
    private PollingCounter? _vacuumProgressCounter;
    private PollingCounter? _writerWaitDurationCounter;
    private PollingCounter? _writerContentionCounter;
    private PollingCounter? _activeSnapshotCountCounter;
    private PollingCounter? _oldestSnapshotAgeCounter;
    private PollingCounter? _rebuildActiveCounter;
    private PollingCounter? _garbageCollectionActiveCounter;

    private IncrementingPollingCounter? _walBytesPerSecCounter;
    private IncrementingPollingCounter? _txCommitPerSecCounter;
    private IncrementingPollingCounter? _txAbortPerSecCounter;
    private IncrementingPollingCounter? _crashRecoveryRateCounter;

    private QuiverEventSource() { }

    // ============================================================================
    // Structured diagnostic events
    // ============================================================================

    [Event(
        1,
        Level = EventLevel.Informational,
        Message = "tx {0} committed in {1} ms")]
    public void TxCommitted(long txId, double durationMs)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
            WriteEvent(1, txId, durationMs);
    }

    [Event(
        2,
        Level = EventLevel.Warning,
        Message = "tx {0} aborted in {1} ms")]
    public void TxAborted(long txId, double durationMs)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
            WriteEvent(2, txId, durationMs);
    }

    [Event(
        3,
        Level = EventLevel.Error,
        Message = "tx {0} commit failed: {1} ({2})")]
    public void TxCommitFailed(long txId, string reason, string exceptionType)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
            WriteEvent(3, txId, reason ?? "", exceptionType ?? "");
    }

    [Event(
        10,
        Level = EventLevel.Informational,
        Message = "checkpoint beginLsn={0} completed in {1} ms")]
    public void CheckpointCompleted(long beginLsn, double durationMs)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
            WriteEvent(10, beginLsn, durationMs);
    }

    [Event(
        20,
        Level = EventLevel.Verbose,
        Message = "wal flush targetLsn={0} completed in {1} ms")]
    public void WalFlushed(long targetLsn, double durationMs)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
            WriteEvent(20, targetLsn, durationMs);
    }

    [Event(
        30,
        Level = EventLevel.Verbose,
        Message = "query executed: tx={0} rows={1} in {2} ms")]
    public void QueryExecuted(long txId, int rows, double durationMs)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
            WriteEvent(30, txId, rows, durationMs);
    }

    [Event(
        40,
        Level = EventLevel.Warning,
        Message = "old snapshot: active={0}, age={1}s, start={2}, committedHighWater={3}")]
    public void OldSnapshotDetected(
        int activeCount,
        double oldestAgeSeconds,
        string startLocation,
        long committedHighWater)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
            WriteEvent(
                40,
                activeCount,
                oldestAgeSeconds,
                startLocation ?? "unknown",
                committedHighWater);
    }

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
        // ------- gauge -------
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
        _writerWaitDurationCounter ??= new PollingCounter(
            "writer-wait-duration-ms",
            this,
            () => Volatile.Read(ref _writerWaitDurationMicroseconds) / 1000.0)
        {
            DisplayName = "Writer lease wait duration (running total)",
            DisplayUnits = "ms",
        };
        _writerContentionCounter ??= new PollingCounter(
            "writer-contention-count",
            this,
            () => Volatile.Read(ref _writerContentionCount))
        {
            DisplayName = "Writer lease contentions (running total)",
            DisplayUnits = "contentions",
        };
        _activeSnapshotCountCounter ??= new PollingCounter(
            "active-snapshot-count",
            this,
            () => SumSnapshotProviders(static provider => provider.ActiveCount()))
        {
            DisplayName = "Active reader snapshots",
            DisplayUnits = "snapshots",
        };
        _oldestSnapshotAgeCounter ??= new PollingCounter(
            "oldest-snapshot-age-seconds",
            this,
            () => MaxSnapshotProviders(static provider => provider.OldestAgeSeconds()))
        {
            DisplayName = "Oldest reader snapshot age",
            DisplayUnits = "seconds",
        };
        _rebuildActiveCounter ??= new PollingCounter(
            "maintenance-rebuild-active",
            this,
            () => Volatile.Read(ref _rebuildActive))
        {
            DisplayName = "Active derived index rebuild operations",
        };
        _garbageCollectionActiveCounter ??= new PollingCounter(
            "maintenance-gc-active",
            this,
            () => Volatile.Read(ref _garbageCollectionActive))
        {
            DisplayName = "Active garbage collection operations",
        };

        // ------- rate counter (累計 → 毎秒) -------
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
    [NonEvent]
    public void BufferPoolHit() => Interlocked.Increment(ref _bufferPoolHits);

    /// <summary>BufferPool フレームミス (1 回 = 1)。</summary>
    [NonEvent]
    public void BufferPoolMiss() => Interlocked.Increment(ref _bufferPoolMisses);

    /// <summary>BufferPool eviction (1 回 = 1)。フレーム入れ替えで dirty が書き出された場合に呼ぶ。</summary>
    [NonEvent]
    public void BufferPoolEviction() => Interlocked.Increment(ref _bufferPoolEvictions);

    /// <summary>WAL に書いたバイト数を加算する。</summary>
    [NonEvent]
    public void WalBytesWritten(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _walBytesWritten, bytes);
    }

    /// <summary>WAL flush 要求の保留件数を 1 増やす (<see cref="WalFlushRequestCompleted"/> と対で呼ぶ)。</summary>
    [NonEvent]
    public void WalFlushRequestStarted() => Interlocked.Increment(ref _walPendingFlushRequests);

    /// <summary>WAL flush 要求の保留件数を 1 減らす。</summary>
    [NonEvent]
    public void WalFlushRequestCompleted() => Interlocked.Decrement(ref _walPendingFlushRequests);

    /// <summary>トランザクション commit 完了 (1 回 = 1)。</summary>
    [NonEvent]
    public void TxCommit() => Interlocked.Increment(ref _txCommitCount);

    /// <summary>トランザクション abort 完了 (1 回 = 1)。</summary>
    [NonEvent]
    public void TxAbort() => Interlocked.Increment(ref _txAbortCount);

    [NonEvent]
    public void WriterContention()
        => Interlocked.Increment(ref _writerContentionCount);

    [NonEvent]
    public void WriterWaitCompleted(double durationMs)
    {
        if (durationMs > 0)
            Interlocked.Add(
                ref _writerWaitDurationMicroseconds,
                (long)Math.Round(durationMs * 1000));
    }

    [NonEvent]
    public void RebuildStarted() => Interlocked.Increment(ref _rebuildActive);

    [NonEvent]
    public void RebuildCompleted() => Interlocked.Decrement(ref _rebuildActive);

    [NonEvent]
    public void GarbageCollectionStarted()
        => Interlocked.Increment(ref _garbageCollectionActive);

    [NonEvent]
    public void GarbageCollectionCompleted()
        => Interlocked.Decrement(ref _garbageCollectionActive);

    /// <summary>crash recovery が起動した (1 回 = 1)。<see cref="Quiver.Transactions.RecoveryManager.Recover"/> で 1 度呼ぶ。</summary>
    [NonEvent]
    public void CrashRecovery() => Interlocked.Increment(ref _crashRecoveryCount);

    /// <summary>
    /// CheckIndexConsistency が観測した orphan 件数を gauge にセットする。
    /// 値は次回 CheckIndexConsistency 呼び出しまで保持される。
    /// </summary>
    [NonEvent]
    public void SetIndexOrphanCount(long count)
        => Volatile.Write(ref _indexOrphanLastObserved, count);

    /// <summary>Vacuum 進捗 (0..100) を gauge にセットする。vacuum 完了時は 0 に戻すこと。</summary>
    [NonEvent]
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

    public IDisposable RegisterSnapshotProvider(
        Func<long> activeCount,
        Func<double> oldestAgeSeconds)
    {
        ArgumentNullException.ThrowIfNull(activeCount);
        ArgumentNullException.ThrowIfNull(oldestAgeSeconds);
        var key = new object();
        _snapshotProviders[key] = new(activeCount, oldestAgeSeconds);
        return new SnapshotRegistration(_snapshotProviders, key);
    }

    // ============================================================================
    // ヘルパー
    // ============================================================================

    private double GetBufferPoolHitRatio()
    {
        long h = Volatile.Read(ref _bufferPoolHits);
        long m = Volatile.Read(ref _bufferPoolMisses);
        long total = h + m;
        return total == 0 ? 0.0 : (double)h / total;
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

    private double SumSnapshotProviders(Func<SnapshotProvider, double> selector)
    {
        double sum = 0;
        foreach (SnapshotProvider provider in _snapshotProviders.Values)
        {
            try { sum += selector(provider); } catch { }
        }
        return sum;
    }

    private double MaxSnapshotProviders(Func<SnapshotProvider, double> selector)
    {
        double maximum = 0;
        foreach (SnapshotProvider provider in _snapshotProviders.Values)
        {
            try { maximum = Math.Max(maximum, selector(provider)); } catch { }
        }
        return maximum;
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

    private sealed record SnapshotProvider(
        Func<long> ActiveCount,
        Func<double> OldestAgeSeconds);

    private sealed class SnapshotRegistration(
        ConcurrentDictionary<object, SnapshotProvider> bag,
        object key) : IDisposable
    {
        private ConcurrentDictionary<object, SnapshotProvider>? _bag = bag;
        private readonly object _key = key;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _bag, null);
            current?.TryRemove(_key, out _);
        }
    }
}

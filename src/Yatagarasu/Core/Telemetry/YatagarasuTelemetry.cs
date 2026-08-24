using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;

namespace Yatagarasu.Telemetry;

/// <summary>
/// Yatagarasu 全体の OpenTelemetry 計装ポイント。
/// <c>System.Diagnostics.ActivitySource</c> / <c>System.Diagnostics.Metrics.Meter</c>
/// は BCL に含まれており、ここでは OpenTelemetry SDK への依存を持たない。
/// <c>Yatagarasu.OpenTelemetry</c> パッケージが本クラスが公開する Source / Meter 名を
/// <c>TracerProviderBuilder.AddSource(...)</c> / <c>MeterProviderBuilder.AddMeter(...)</c>
/// に登録することで、外部 OTel コレクタ (Jaeger / Prometheus 等) へ計装が流れる。
/// </summary>
public static class YatagarasuTelemetry
{
    private static readonly ConcurrentDictionary<object, SnapshotMetricProvider>
        SnapshotMetricProviders = new();
    /// <summary>本ライブラリのアセンブリバージョン (Activity / Meter のタグ用)。</summary>
    public const string Version = "1.0.0";

    // ---------------------------------------------------------------------
    // ActivitySource (トレース)
    // ---------------------------------------------------------------------

    /// <summary>Transaction の Begin / Commit / Abort を表す span 用。</summary>
    public const string TransactionSourceName = "Yatagarasu.Transaction";

    /// <summary>Query 実行 (オペレータパイプライン) を表す span 用。</summary>
    public const string QuerySourceName = "Yatagarasu.Query";

    /// <summary>Checkpoint (sharp checkpoint) を表す span 用。</summary>
    public const string CheckpointSourceName = "Yatagarasu.Checkpoint";

    /// <summary>WAL flush (fsync) を表す span 用。</summary>
    public const string WalFlushSourceName = "Yatagarasu.WalFlush";

    /// <summary>本ライブラリが公開する全 ActivitySource 名。</summary>
    public static readonly string[] AllSourceNames =
    {
        TransactionSourceName,
        QuerySourceName,
        CheckpointSourceName,
        WalFlushSourceName,
    };

    /// <summary>Transaction の span を生成する <see cref="ActivitySource"/>。</summary>
    public static readonly ActivitySource TransactionActivitySource =
        new(TransactionSourceName, Version);

    /// <summary>Query の span を生成する <see cref="ActivitySource"/>。</summary>
    public static readonly ActivitySource QueryActivitySource =
        new(QuerySourceName, Version);

    /// <summary>Checkpoint の span を生成する <see cref="ActivitySource"/>。</summary>
    public static readonly ActivitySource CheckpointActivitySource =
        new(CheckpointSourceName, Version);

    /// <summary>WAL flush の span を生成する <see cref="ActivitySource"/>。</summary>
    public static readonly ActivitySource WalFlushActivitySource =
        new(WalFlushSourceName, Version);

    // ---------------------------------------------------------------------
    // Meter (メトリクス)
    // ---------------------------------------------------------------------

    /// <summary>本ライブラリが公開する <see cref="Meter"/> の名前。</summary>
    public const string MeterName = "Yatagarasu";

    /// <summary>histogram / counter を発行する Meter。</summary>
    public static readonly Meter Meter = new(MeterName, Version);

    /// <summary>tx.commit の所要時間 (ms)。</summary>
    public static readonly Histogram<double> TxCommitDurationMs =
        Meter.CreateHistogram<double>(
            name: "yatagarasu.tx.commit.duration",
            unit: "ms",
            description: "Duration of a successful transaction commit (Commit() 全体)。");

    /// <summary>tx.abort の所要時間 (ms)。</summary>
    public static readonly Histogram<double> TxAbortDurationMs =
        Meter.CreateHistogram<double>(
            name: "yatagarasu.tx.abort.duration",
            unit: "ms",
            description: "Duration of a transaction abort (Abort() 全体)。");

    /// <summary>Checkpoint 全体の所要時間 (ms)。</summary>
    public static readonly Histogram<double> CheckpointDurationMs =
        Meter.CreateHistogram<double>(
            name: "yatagarasu.checkpoint.duration",
            unit: "ms",
            description: "Duration of a sharp checkpoint (Begin sentinel → Truncate 完了)。");

    /// <summary>WAL flush (1 回の fsync) の所要時間 (ms)。</summary>
    public static readonly Histogram<double> WalFlushDurationMs =
        Meter.CreateHistogram<double>(
            name: "yatagarasu.wal.flush.duration",
            unit: "ms",
            description: "Duration of a single WAL flush (fsync) batch。");

    /// <summary>クエリ実行 (パイプライン全体) の所要時間 (ms)。</summary>
    public static readonly Histogram<double> QueryDurationMs =
        Meter.CreateHistogram<double>(
            name: "yatagarasu.query.duration",
            unit: "ms",
            description: "Duration of a single query pipeline execution。");

    /// <summary>WAL に書き込んだバイト数 (累計)。</summary>
    public static readonly Counter<long> WalBytesWritten =
        Meter.CreateCounter<long>(
            name: "yatagarasu.wal.bytes",
            unit: "By",
            description: "Total bytes appended to WAL across all transactions。");

    /// <summary>buffer pool ヒット数 (累計)。</summary>
    public static readonly Counter<long> BufferPoolHits =
        Meter.CreateCounter<long>(
            name: "yatagarasu.buffer_pool.hits",
            unit: "{hit}",
            description: "Buffer-pool frame lookups satisfied by an existing frame。");

    /// <summary>buffer pool ミス数 (累計、I/O 発生)。</summary>
    public static readonly Counter<long> BufferPoolMisses =
        Meter.CreateCounter<long>(
            name: "yatagarasu.buffer_pool.misses",
            unit: "{miss}",
            description: "Buffer-pool frame lookups requiring eviction + page-in。");

    /// <summary>tx.commit 件数 (累計)。</summary>
    public static readonly Counter<long> TxCommitCount =
        Meter.CreateCounter<long>(
            name: "yatagarasu.tx.commit.count",
            unit: "{tx}",
            description: "Total number of transactions committed successfully。");

    /// <summary>tx.abort 件数 (累計)。</summary>
    public static readonly Counter<long> TxAbortCount =
        Meter.CreateCounter<long>(
            name: "yatagarasu.tx.abort.count",
            unit: "{tx}",
            description: "Total number of transactions that aborted (rollback or error)。");

    /// <summary>writer lease の取得待ち時間 (ミリ秒)。競合した取得だけを記録する。</summary>
    public static readonly Histogram<double> WriterWaitDurationMs =
        Meter.CreateHistogram<double>(
            name: "yatagarasu.writer.wait.duration",
            unit: "ms",
            description: "Duration spent waiting for the database writer lease。");

    /// <summary>writer lease の競合を検出した回数。</summary>
    public static readonly Counter<long> WriterContentionCount =
        Meter.CreateCounter<long>(
            name: "yatagarasu.writer.contention.count",
            unit: "{contention}",
            description: "Total writer lease acquisition attempts that observed contention。");

    /// <summary>プロセス内で active な reader snapshot 数。</summary>
    public static readonly ObservableGauge<long> ActiveSnapshotCount =
        Meter.CreateObservableGauge(
            name: "yatagarasu.snapshot.active.count",
            observeValue: ObserveActiveSnapshotCount,
            unit: "{snapshot}",
            description: "Current number of active reader snapshots。");

    /// <summary>プロセス内で最も古い reader snapshot の経過時間 (秒)。</summary>
    public static readonly ObservableGauge<double> OldestSnapshotAgeSeconds =
        Meter.CreateObservableGauge(
            name: "yatagarasu.snapshot.oldest.age",
            observeValue: ObserveOldestSnapshotAge,
            unit: "s",
            description: "Age of the oldest active reader snapshot。");

    /// <summary>実行中の derived index rebuild 数。</summary>
    public static readonly UpDownCounter<long> MaintenanceRebuildActive =
        Meter.CreateUpDownCounter<long>(
            name: "yatagarasu.maintenance.rebuild.active",
            unit: "{operation}",
            description: "Current number of derived index rebuild operations。");

    /// <summary>実行中の garbage collection 数。</summary>
    public static readonly UpDownCounter<long> MaintenanceGarbageCollectionActive =
        Meter.CreateUpDownCounter<long>(
            name: "yatagarasu.maintenance.gc.active",
            unit: "{operation}",
            description: "Current number of maintenance garbage collection operations。");

    internal static IDisposable RegisterSnapshotProvider(
        Func<long> activeCount,
        Func<double> oldestAgeSeconds)
    {
        ArgumentNullException.ThrowIfNull(activeCount);
        ArgumentNullException.ThrowIfNull(oldestAgeSeconds);
        var key = new object();
        SnapshotMetricProviders[key] = new(activeCount, oldestAgeSeconds);
        return new ProviderRegistration(key);
    }

    internal static IDisposable TrackRebuild()
        => new MaintenanceRegistration(rebuild: true);

    internal static IDisposable TrackGarbageCollection()
        => new MaintenanceRegistration(rebuild: false);

    private static long ObserveActiveSnapshotCount()
        => SnapshotMetricProviders.Values.Sum(static provider =>
        {
            try { return provider.ActiveCount(); }
            catch { return 0; }
        });

    private static double ObserveOldestSnapshotAge()
    {
        double oldest = 0;
        foreach (SnapshotMetricProvider provider in SnapshotMetricProviders.Values)
        {
            try { oldest = Math.Max(oldest, provider.OldestAgeSeconds()); }
            catch { }
        }
        return oldest;
    }

    private sealed record SnapshotMetricProvider(
        Func<long> ActiveCount,
        Func<double> OldestAgeSeconds);

    private sealed class ProviderRegistration(object key) : IDisposable
    {
        private object? _key = key;

        public void Dispose()
        {
            object? keyToRemove = Interlocked.Exchange(ref _key, null);
            if (keyToRemove is not null)
                SnapshotMetricProviders.TryRemove(keyToRemove, out _);
        }
    }

    private sealed class MaintenanceRegistration : IDisposable
    {
        private readonly bool _rebuild;
        private int _active = 1;

        internal MaintenanceRegistration(bool rebuild)
        {
            _rebuild = rebuild;
            if (rebuild)
            {
                MaintenanceRebuildActive.Add(1);
                YatagarasuEventSource.Log.RebuildStarted();
            }
            else
            {
                MaintenanceGarbageCollectionActive.Add(1);
                YatagarasuEventSource.Log.GarbageCollectionStarted();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0)
                return;
            if (_rebuild)
            {
                MaintenanceRebuildActive.Add(-1);
                YatagarasuEventSource.Log.RebuildCompleted();
            }
            else
            {
                MaintenanceGarbageCollectionActive.Add(-1);
                YatagarasuEventSource.Log.GarbageCollectionCompleted();
            }
        }
    }
}

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Quiver.Telemetry;

/// <summary>
/// Quiver 全体の OpenTelemetry 計装ポイント。
/// <c>System.Diagnostics.ActivitySource</c> / <c>System.Diagnostics.Metrics.Meter</c>
/// は BCL に含まれており、ここでは OpenTelemetry SDK への依存を持たない。
/// <c>Quiver.OpenTelemetry</c> パッケージが本クラスが公開する Source / Meter 名を
/// <c>TracerProviderBuilder.AddSource(...)</c> / <c>MeterProviderBuilder.AddMeter(...)</c>
/// に登録することで、外部 OTel コレクタ (Jaeger / Prometheus 等) へ計装が流れる。
/// </summary>
public static class QuiverTelemetry
{
    /// <summary>本ライブラリのアセンブリバージョン (Activity / Meter のタグ用)。</summary>
    public const string Version = "1.0.0";

    // ---------------------------------------------------------------------
    // ActivitySource (Tracing)
    // ---------------------------------------------------------------------

    /// <summary>Transaction の Begin / Commit / Abort を表す span 用。</summary>
    public const string TransactionSourceName = "Quiver.Transaction";

    /// <summary>Query 実行 (オペレータパイプライン) を表す span 用。</summary>
    public const string QuerySourceName = "Quiver.Query";

    /// <summary>Checkpoint (sharp checkpoint) を表す span 用。</summary>
    public const string CheckpointSourceName = "Quiver.Checkpoint";

    /// <summary>WAL flush (fsync) を表す span 用。</summary>
    public const string WalFlushSourceName = "Quiver.WalFlush";

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
    // Meter (Metrics)
    // ---------------------------------------------------------------------

    /// <summary>本ライブラリが公開する <see cref="Meter"/> の名前。</summary>
    public const string MeterName = "Quiver";

    /// <summary>histogram / counter を発行する Meter。</summary>
    public static readonly Meter Meter = new(MeterName, Version);

    /// <summary>tx.commit の所要時間 (ms)。</summary>
    public static readonly Histogram<double> TxCommitDurationMs =
        Meter.CreateHistogram<double>(
            name: "quiver.tx.commit.duration",
            unit: "ms",
            description: "Duration of a successful transaction commit (Commit() 全体)。");

    /// <summary>tx.abort の所要時間 (ms)。</summary>
    public static readonly Histogram<double> TxAbortDurationMs =
        Meter.CreateHistogram<double>(
            name: "quiver.tx.abort.duration",
            unit: "ms",
            description: "Duration of a transaction abort (Abort() 全体)。");

    /// <summary>Checkpoint 全体の所要時間 (ms)。</summary>
    public static readonly Histogram<double> CheckpointDurationMs =
        Meter.CreateHistogram<double>(
            name: "quiver.checkpoint.duration",
            unit: "ms",
            description: "Duration of a sharp checkpoint (Begin sentinel → Truncate 完了)。");

    /// <summary>WAL flush (1 回の fsync) の所要時間 (ms)。</summary>
    public static readonly Histogram<double> WalFlushDurationMs =
        Meter.CreateHistogram<double>(
            name: "quiver.wal.flush.duration",
            unit: "ms",
            description: "Duration of a single WAL flush (fsync) batch。");

    /// <summary>クエリ実行 (パイプライン全体) の所要時間 (ms)。</summary>
    public static readonly Histogram<double> QueryDurationMs =
        Meter.CreateHistogram<double>(
            name: "quiver.query.duration",
            unit: "ms",
            description: "Duration of a single query pipeline execution。");

    /// <summary>ロック取得待ち時間 (ms)。</summary>
    public static readonly Histogram<double> LockWaitMs =
        Meter.CreateHistogram<double>(
            name: "quiver.lock.wait.duration",
            unit: "ms",
            description: "Time spent waiting for a node / relationship / index lock。");

    /// <summary>WAL に書き込んだバイト数 (累計)。</summary>
    public static readonly Counter<long> WalBytesWritten =
        Meter.CreateCounter<long>(
            name: "quiver.wal.bytes",
            unit: "By",
            description: "Total bytes appended to WAL across all transactions。");

    /// <summary>buffer pool ヒット数 (累計)。</summary>
    public static readonly Counter<long> BufferPoolHits =
        Meter.CreateCounter<long>(
            name: "quiver.buffer_pool.hits",
            unit: "{hit}",
            description: "Buffer-pool frame lookups satisfied by an existing frame。");

    /// <summary>buffer pool ミス数 (累計、I/O 発生)。</summary>
    public static readonly Counter<long> BufferPoolMisses =
        Meter.CreateCounter<long>(
            name: "quiver.buffer_pool.misses",
            unit: "{miss}",
            description: "Buffer-pool frame lookups requiring eviction + page-in。");

    /// <summary>tx.commit 件数 (累計)。</summary>
    public static readonly Counter<long> TxCommitCount =
        Meter.CreateCounter<long>(
            name: "quiver.tx.commit.count",
            unit: "{tx}",
            description: "Total number of transactions committed successfully。");

    /// <summary>tx.abort 件数 (累計)。</summary>
    public static readonly Counter<long> TxAbortCount =
        Meter.CreateCounter<long>(
            name: "quiver.tx.abort.count",
            unit: "{tx}",
            description: "Total number of transactions that aborted (rollback or error)。");
}

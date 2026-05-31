using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quiver.Telemetry;

/// <summary>
/// OB-3: 構造化ログのエントリポイント。
/// ホット path に <c>logger.BeginScope</c> + <see cref="LoggerMessageAttribute"/> による
/// allocation-free ログを通すための薄いファサード。
/// </summary>
/// <remarks>
/// <see cref="LoggerFactory"/> 未設定時は <see cref="NullLoggerFactory.Instance"/> に
/// フォールバックする。ホット path はカテゴリ別の <see cref="ILogger"/> を直接参照し、
/// <c>NullLogger</c> 経路では <see cref="ILogger.IsEnabled"/> が false を返すため
/// LoggerMessage 生成コードはすぐに帰る — 計装オーバーヘッドはほぼゼロに保たれる。
///
/// 設計判断: <see cref="QuiverTelemetry"/> (ActivitySource / Meter) と同じく
/// プロセス静的シングルトンに揃えた。マルチ DB 構成でも観測シンクは集約される想定。
/// </remarks>
public static partial class QuiverLog
{
    /// <summary>tx 境界用ロガーのカテゴリ名。</summary>
    public const string TransactionCategory = "Quiver.Transaction";

    /// <summary>クエリ実行用ロガーのカテゴリ名。</summary>
    public const string QueryCategory = "Quiver.Query";

    /// <summary>チェックポイント用ロガーのカテゴリ名。</summary>
    public const string CheckpointCategory = "Quiver.Checkpoint";

    /// <summary>WAL flush 用ロガーのカテゴリ名。</summary>
    public const string WalCategory = "Quiver.Wal";

    /// <summary>page-level slow path 用ロガーのカテゴリ名。</summary>
    public const string StorageCategory = "Quiver.Storage";

    private static volatile ILoggerFactory s_loggerFactory = NullLoggerFactory.Instance;
    private static volatile ILogger s_transactionLogger = NullLogger.Instance;
    private static volatile ILogger s_queryLogger = NullLogger.Instance;
    private static volatile ILogger s_checkpointLogger = NullLogger.Instance;
    private static volatile ILogger s_walLogger = NullLogger.Instance;
    private static volatile ILogger s_storageLogger = NullLogger.Instance;

    /// <summary>
    /// ホット path 各所に注入する <see cref="ILoggerFactory"/>。
    /// <c>null</c> を設定すると <see cref="NullLoggerFactory.Instance"/> に戻る。
    /// </summary>
    /// <remarks>
    /// <c>GraphDatabase.Open</c> が起動時に
    /// <c>GraphDatabaseOptions.LoggerFactory</c> をここへ反映する。マルチ DB 構成では
    /// 最後に Open された DB の factory が勝つが、観測パイプラインを各 DB ごとに
    /// 分離するユースケースは現在想定外なので妥協する (OTel / EventSource と同じ前提)。
    /// </remarks>
    public static ILoggerFactory LoggerFactory
    {
        get => s_loggerFactory;
        set
        {
            var factory = value ?? NullLoggerFactory.Instance;
            s_loggerFactory = factory;
            s_transactionLogger = factory.CreateLogger(TransactionCategory);
            s_queryLogger = factory.CreateLogger(QueryCategory);
            s_checkpointLogger = factory.CreateLogger(CheckpointCategory);
            s_walLogger = factory.CreateLogger(WalCategory);
            s_storageLogger = factory.CreateLogger(StorageCategory);
        }
    }

    /// <summary>tx commit / abort 境界に使うロガー。</summary>
    public static ILogger TransactionLogger => s_transactionLogger;

    /// <summary>クエリ実行に使うロガー。</summary>
    public static ILogger QueryLogger => s_queryLogger;

    /// <summary>チェックポイントに使うロガー。</summary>
    public static ILogger CheckpointLogger => s_checkpointLogger;

    /// <summary>WAL flush に使うロガー。</summary>
    public static ILogger WalLogger => s_walLogger;

    /// <summary>page-level slow path に使うロガー。</summary>
    public static ILogger StorageLogger => s_storageLogger;

    /// <summary>
    /// tx 境界用 scope。<c>quiver.tx.id</c> + <c>quiver.op</c> を構造化キーとして展開し、
    /// 同一 tx の Begin → operator → Commit を後で <c>quiver.tx.id</c> で grep できるようにする。
    /// </summary>
    /// <returns>
    /// <see cref="ILogger.IsEnabled(LogLevel)"/> が false の場合は <c>null</c>。
    /// 呼び出し側は <c>using var _ = QuiverLog.BeginTxScope(...)</c> でラップすれば
    /// null でも Dispose 例外なく no-op になる。
    /// </returns>
    public static IDisposable? BeginTxScope(ILogger logger, long txId, string operation)
    {
        if (logger is null || !logger.IsEnabled(LogLevel.Information)) return null;
        return logger.BeginScope(new TxScopeState(txId, operation));
    }

    /// <summary>
    /// クエリ境界用 scope。<c>quiver.tx.id</c> + <c>quiver.op</c> を展開する。
    /// Debug 以上が有効な時のみアロケートする。
    /// </summary>
    public static IDisposable? BeginQueryScope(ILogger logger, long txId, string operation = "Execute")
    {
        if (logger is null || !logger.IsEnabled(LogLevel.Debug)) return null;
        return logger.BeginScope(new TxScopeState(txId, operation));
    }

    /// <summary>
    /// 構造化スコープ。<see cref="IReadOnlyList{T}"/> として展開すると主要な構造化シンク
    /// (Serilog / OTel logs / Console formatter) が <c>quiver.tx.id</c> / <c>quiver.op</c>
    /// をキーとして拾える。インデクサは hot path で box せずに済むよう struct のままにしている。
    /// </summary>
    internal readonly struct TxScopeState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly long _txId;
        private readonly string _op;

        public TxScopeState(long txId, string op)
        {
            _txId = txId;
            _op = op;
        }

        public int Count => 2;

        public KeyValuePair<string, object?> this[int index] => index switch
        {
            0 => new KeyValuePair<string, object?>("quiver.tx.id", _txId),
            1 => new KeyValuePair<string, object?>("quiver.op", _op),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return new KeyValuePair<string, object?>("quiver.tx.id", _txId);
            yield return new KeyValuePair<string, object?>("quiver.op", _op);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => $"tx={_txId} op={_op}";
    }

    // ----- LoggerMessage source-generated methods (allocation-free) -----

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "tx {TxId} committed in {DurationMs:F2} ms")]
    public static partial void TxCommitted(ILogger logger, long txId, double durationMs);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "tx {TxId} aborted in {DurationMs:F2} ms")]
    public static partial void TxAborted(ILogger logger, long txId, double durationMs);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "tx {TxId} commit failed: {Reason}")]
    public static partial void TxCommitFailed(ILogger logger, long txId, string reason, Exception? ex);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "checkpoint beginLsn={BeginLsn} completed in {DurationMs:F2} ms")]
    public static partial void CheckpointCompleted(ILogger logger, long beginLsn, double durationMs);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Trace,
        Message = "wal flush targetLsn={TargetLsn} completed in {DurationMs:F2} ms")]
    public static partial void WalFlushed(ILogger logger, long targetLsn, double durationMs);

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Debug,
        Message = "query executed: rows={Rows} in {DurationMs:F2} ms")]
    public static partial void QueryExecuted(ILogger logger, int rows, double durationMs);
}

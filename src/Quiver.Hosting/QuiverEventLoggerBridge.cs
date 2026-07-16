using System.Collections;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

namespace Quiver.Hosting;

/// <summary>
/// Quiver core の process-wide EventSource イベントを Microsoft.Extensions.Logging へ転送する。
/// </summary>
/// <remarks>
/// EventSource 自体がプロセス単位なので、イベントは特定の <c>QuiverDatabase</c> インスタンスには
/// ルーティングされない。通常の Generic Host 構成では本 listener を 1 個だけ生成する。
/// </remarks>
internal sealed class QuiverEventLoggerBridge : EventListener
{
    private const string SourceName = "Quiver-EventSource";
    private const string TransactionCategory = "Quiver.Transaction";
    private const string QueryCategory = "Quiver.Query";
    private const string CheckpointCategory = "Quiver.Checkpoint";
    private const string WalCategory = "Quiver.Wal";

    private readonly ILogger? _transactionLogger;
    private readonly ILogger? _queryLogger;
    private readonly ILogger? _checkpointLogger;
    private readonly ILogger? _walLogger;

    public QuiverEventLoggerBridge(ILoggerFactory? loggerFactory)
    {
        if (loggerFactory is null) return;
        _transactionLogger = loggerFactory.CreateLogger(TransactionCategory);
        _queryLogger = loggerFactory.CreateLogger(QueryCategory);
        _checkpointLogger = loggerFactory.CreateLogger(CheckpointCategory);
        _walLogger = loggerFactory.CreateLogger(WalCategory);

        // EventListener の基底 constructor 中は logger fields が未初期化なので、既存 source は
        // 初期化完了後に明示的に有効化する。以後に作られる source は OnEventSourceCreated が拾う。
        foreach (var source in EventSource.GetSources())
            EnableIfQuiver(source);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (_transactionLogger is not null)
            EnableIfQuiver(eventSource);
    }

    private void EnableIfQuiver(EventSource eventSource)
    {
        if (string.Equals(eventSource.Name, SourceName, StringComparison.Ordinal))
            EnableEvents(eventSource, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_transactionLogger is null) return;
        try
        {
            switch (eventData.EventId)
            {
                case 1:
                    LogTransactionCompleted(eventData, committed: true);
                    break;
                case 2:
                    LogTransactionCompleted(eventData, committed: false);
                    break;
                case 3:
                    LogTransactionFailure(eventData);
                    break;
                case 10:
                    LogCheckpoint(eventData);
                    break;
                case 20:
                    LogWalFlush(eventData);
                    break;
                case 30:
                    LogQuery(eventData);
                    break;
            }
        }
        catch
        {
            // 診断 sink の失敗で DB 操作を失敗させない。
        }
    }

    private void LogTransactionCompleted(EventWrittenEventArgs data, bool committed)
    {
        long txId = Int64At(data, 0);
        double durationMs = DoubleAt(data, 1);
        string operation = committed ? "Commit" : "Abort";
        string verb = committed ? "committed" : "aborted";
        var level = committed ? LogLevel.Information : LogLevel.Warning;
        int eventId = committed ? 1 : 2;

        using var scope = _transactionLogger!.BeginScope(
            LogState.Scope(("quiver.tx.id", txId), ("quiver.op", operation)));
        Log(
            _transactionLogger,
            level,
            eventId,
            committed ? "TxCommitted" : "TxAborted",
            $"tx {txId} {verb} in {durationMs:F2} ms",
            ("TxId", txId),
            ("DurationMs", durationMs));
    }

    private void LogTransactionFailure(EventWrittenEventArgs data)
    {
        long txId = Int64At(data, 0);
        string reason = StringAt(data, 1);
        string exceptionType = StringAt(data, 2);
        using var scope = _transactionLogger!.BeginScope(
            LogState.Scope(("quiver.tx.id", txId), ("quiver.op", "Commit")));
        Log(
            _transactionLogger,
            LogLevel.Error,
            3,
            "TxCommitFailed",
            $"tx {txId} commit failed: {reason}",
            ("TxId", txId),
            ("Reason", reason),
            ("ExceptionType", exceptionType));
    }

    private void LogCheckpoint(EventWrittenEventArgs data)
    {
        long beginLsn = Int64At(data, 0);
        double durationMs = DoubleAt(data, 1);
        Log(
            _checkpointLogger!,
            LogLevel.Information,
            10,
            "CheckpointCompleted",
            $"checkpoint beginLsn={beginLsn} completed in {durationMs:F2} ms",
            ("BeginLsn", beginLsn),
            ("DurationMs", durationMs));
    }

    private void LogWalFlush(EventWrittenEventArgs data)
    {
        long targetLsn = Int64At(data, 0);
        double durationMs = DoubleAt(data, 1);
        Log(
            _walLogger!,
            LogLevel.Trace,
            20,
            "WalFlushed",
            $"wal flush targetLsn={targetLsn} completed in {durationMs:F2} ms",
            ("TargetLsn", targetLsn),
            ("DurationMs", durationMs));
    }

    private void LogQuery(EventWrittenEventArgs data)
    {
        long txId = Int64At(data, 0);
        int rows = Int32At(data, 1);
        double durationMs = DoubleAt(data, 2);
        using var scope = _queryLogger!.BeginScope(
            LogState.Scope(("quiver.tx.id", txId), ("quiver.op", "Execute")));
        Log(
            _queryLogger,
            LogLevel.Debug,
            30,
            "QueryExecuted",
            $"query executed: rows={rows} in {durationMs:F2} ms",
            ("TxId", txId),
            ("Rows", rows),
            ("DurationMs", durationMs));
    }

    private static void Log(
        ILogger logger,
        LogLevel level,
        int eventId,
        string eventName,
        string message,
        params (string Key, object? Value)[] values)
    {
        if (!logger.IsEnabled(level)) return;
        logger.Log(
            level,
            new EventId(eventId, eventName),
            new LogState(message, values),
            exception: null,
            static (state, _) => state.ToString());
    }

    private static long Int64At(EventWrittenEventArgs data, int index)
        => Convert.ToInt64(data.Payload![index], System.Globalization.CultureInfo.InvariantCulture);

    private static int Int32At(EventWrittenEventArgs data, int index)
        => Convert.ToInt32(data.Payload![index], System.Globalization.CultureInfo.InvariantCulture);

    private static double DoubleAt(EventWrittenEventArgs data, int index)
        => Convert.ToDouble(data.Payload![index], System.Globalization.CultureInfo.InvariantCulture);

    private static string StringAt(EventWrittenEventArgs data, int index)
        => Convert.ToString(data.Payload![index], System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private sealed class LogState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly string _message;
        private readonly KeyValuePair<string, object?>[] _values;

        public LogState(string message, params (string Key, object? Value)[] values)
        {
            _message = message;
            _values = new KeyValuePair<string, object?>[values.Length + 1];
            for (int i = 0; i < values.Length; i++)
                _values[i] = new KeyValuePair<string, object?>(values[i].Key, values[i].Value);
            _values[^1] = new KeyValuePair<string, object?>("{OriginalFormat}", message);
        }

        public static LogState Scope(params (string Key, object? Value)[] values)
            => new("", values);

        public int Count => _values.Length;

        public KeyValuePair<string, object?> this[int index] => _values[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => _message;
    }
}

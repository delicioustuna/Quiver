using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quiver.Core.Telemetry;
using Quiver.Operators;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// OB-3: 構造化ログ強化の動作確認。
/// <see cref="QuiverLog"/> 経由でホット path に流れる log/scope を捕捉用ロガーで集めて検証する。
/// </summary>
[Collection("StructuredLoggingTests")] // QuiverLog.LoggerFactory はプロセス静的なので直列化する
public sealed class StructuredLoggingTests : IDisposable
{
    private readonly string _dir;
    private readonly CapturingLoggerProvider _provider;
    private readonly ILoggerFactory _factory;
    private readonly GraphDatabase _db;

    public StructuredLoggingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_oblog_" + Guid.NewGuid().ToString("N"));
        _provider = new CapturingLoggerProvider();
        _factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(_provider);
        });
        _db = GraphDatabase.Open(_dir, new GraphDatabaseOptions { LoggerFactory = _factory });
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
        // プロセス静的を NullLoggerFactory に戻し、他テストへの汚染を防ぐ。
        QuiverLog.LoggerFactory = NullLoggerFactory.Instance;
        if (Directory.Exists(_dir))
            try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Commit_emits_information_log_with_tx_scope()
    {
        long txId;
        using (var tx = _db.BeginTransaction())
        {
            txId = tx.Id.Value;
            tx.CreateNode("Person");
            tx.Commit();
        }

        // QuiverLog.LoggerFactory はプロセス静的なので、並列テスト中は
        // 同じ tx.id を持つ別 DB のエントリも混ざりうる。少なくとも 1 件は
        // 自分の Commit に対応するエントリが存在し、構造化キーが揃っていれば OK。
        var match = _provider.Entries
            .Where(e => e.Category == QuiverLog.TransactionCategory
                        && e.EventId.Id == 1
                        && GetScopeTxId(e) == txId
                        && e.Message.Contains($"tx {txId} committed"))
            .ToList();
        match.Should().NotBeEmpty("commit が EventId=1 + tx.id={0} で出るはず", txId);

        var entry = match[0];
        entry.LogLevel.Should().Be(LogLevel.Information);
        // BeginScope のキーが構造化ログとして出ていること
        entry.ScopeValues.Should().Contain(kv => kv.Key == "quiver.tx.id");
        entry.ScopeValues.Should().Contain(kv => kv.Key == "quiver.op" && (string)kv.Value! == "Commit");
    }

    [Fact]
    public void Rollback_emits_warning_log_with_abort_scope()
    {
        long txId;
        using (var tx = _db.BeginTransaction())
        {
            txId = tx.Id.Value;
            tx.CreateNode("Person");
            tx.Rollback();
        }

        var match = _provider.Entries
            .Where(e => e.Category == QuiverLog.TransactionCategory
                        && e.EventId.Id == 2
                        && GetScopeTxId(e) == txId
                        && e.ScopeValues.Any(kv => kv.Key == "quiver.op" && (string)kv.Value! == "Abort"))
            .ToList();
        match.Should().NotBeEmpty("abort が EventId=2 + tx.id={0} で出るはず", txId);
        match[0].LogLevel.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void Query_execute_emits_debug_log_with_query_scope_and_row_count()
    {
        long txId;
        using (var tx = _db.BeginTransaction())
        {
            tx.CreateNode("Person");
            tx.CreateNode("Person");
            tx.CreateNode("Person");
            tx.Commit();
        }

        using (var tx = _db.BeginReadOnlyTransaction())
        {
            txId = tx.Id.Value;
            var labelId = _db.Schema.GetOrCreateLabel("Person");
            tx.Execute(new NodeByLabelScanOperator(labelId));
            tx.Commit();
        }

        // tx.id + rows=3 を両方満たすものに絞れば、並列テストとの混信を回避できる。
        var match = _provider.Entries
            .Where(e => e.Category == QuiverLog.QueryCategory
                        && e.EventId.Id == 30
                        && GetScopeTxId(e) == txId
                        && e.Message.Contains("rows=3"))
            .ToList();
        match.Should().NotBeEmpty();
        var entry = match[0];
        entry.LogLevel.Should().Be(LogLevel.Debug);
        entry.ScopeValues.Should().Contain(kv => kv.Key == "quiver.op" && (string)kv.Value! == "Execute");
    }

    [Fact]
    public void Multiple_tx_can_be_filtered_by_tx_id_scope_key()
    {
        long firstTxId, secondTxId;
        using (var tx = _db.BeginTransaction())
        {
            firstTxId = tx.Id.Value;
            tx.CreateNode("Person");
            tx.Commit();
        }

        using (var tx = _db.BeginTransaction())
        {
            secondTxId = tx.Id.Value;
            tx.CreateNode("Person");
            tx.Commit();
        }

        firstTxId.Should().NotBe(secondTxId);

        var firstEntries = _provider.Entries
            .Where(e => GetScopeTxId(e) == firstTxId
                        && e.Message.Contains($"tx {firstTxId} committed"))
            .ToList();
        var secondEntries = _provider.Entries
            .Where(e => GetScopeTxId(e) == secondTxId
                        && e.Message.Contains($"tx {secondTxId} committed"))
            .ToList();
        firstEntries.Should().NotBeEmpty("最初の tx のログは quiver.tx.id でフィルタできる");
        secondEntries.Should().NotBeEmpty("2 番目の tx のログも quiver.tx.id でフィルタできる");
    }

    [Fact]
    public void LoggerFactory_null_does_not_throw_and_does_not_reset_existing()
    {
        // 既存 _db は capturing factory を持つ。null 渡しの Open は既存値を維持する。
        var beforeFactory = QuiverLog.LoggerFactory;
        var nullDir = Path.Combine(Path.GetTempPath(), "quiver_oblog_null_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = GraphDatabase.Open(nullDir, new GraphDatabaseOptions { LoggerFactory = null });
            QuiverLog.LoggerFactory.Should().BeSameAs(beforeFactory,
                "null LoggerFactory は既存のロガー設定を上書きしない");
            using var tx = db.BeginTransaction();
            tx.CreateNode("Person");
            tx.Commit();
        }
        finally
        {
            if (Directory.Exists(nullDir))
                try { Directory.Delete(nullDir, recursive: true); } catch { }
        }
    }

    private static long? GetScopeTxId(LogEntry entry)
    {
        foreach (var kv in entry.ScopeValues)
        {
            if (kv.Key == "quiver.tx.id" && kv.Value is long l) return l;
        }
        return null;
    }

    // ===== capturing provider =====

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose() { }
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
    {
        private readonly AsyncLocal<List<IReadOnlyList<KeyValuePair<string, object?>>>> _scopes = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            _scopes.Value ??= new List<IReadOnlyList<KeyValuePair<string, object?>>>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
            {
                _scopes.Value.Add(kvs);
                return new PopOnDispose(_scopes);
            }
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var snapshot = new List<KeyValuePair<string, object?>>();
            if (_scopes.Value is { } scopes)
            {
                foreach (var scope in scopes)
                    foreach (var kv in scope)
                        snapshot.Add(kv);
            }
            owner.Entries.Add(new LogEntry(
                category, logLevel, eventId, formatter(state, exception), exception, snapshot));
        }

        private sealed class PopOnDispose(AsyncLocal<List<IReadOnlyList<KeyValuePair<string, object?>>>> al)
            : IDisposable
        {
            public void Dispose()
            {
                var list = al.Value;
                if (list is { Count: > 0 }) list.RemoveAt(list.Count - 1);
            }
        }
    }

    private sealed record LogEntry(
        string Category,
        LogLevel LogLevel,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> ScopeValues);
}

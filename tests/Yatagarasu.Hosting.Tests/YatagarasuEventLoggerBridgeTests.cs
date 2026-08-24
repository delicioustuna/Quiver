using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yatagarasu.Hosting;
using Xunit;

namespace Yatagarasu.Hosting.Tests;

/// <summary>Yatagarasu.Hosting の EventSource → ILogger 互換ブリッジを検証する。</summary>
public sealed class YatagarasuEventLoggerBridgeTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "yatagarasu_hostlog_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AddYatagarasu_forwards_core_events_to_ILogger_with_scope()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(capture);
        });
        services.AddYatagarasu(options => options.DataDirectory = _dir);

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<GraphStore>();
        store.Write(write => write.CreateVertex("Person"));

        var entry = capture.Entries
            .First(e =>
                e.Category == "Yatagarasu.Transaction"
                && e.EventId.Id == 1);
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("committed");
        entry.ScopeValues.Should().Contain(kv => kv.Key == "yatagarasu.tx.id");
        entry.ScopeValues.Should()
            .Contain(kv => kv.Key == "yatagarasu.op" && Equals(kv.Value, "Commit"));
    }

    [Fact]
    public void AddYatagarasu_without_logging_registration_still_opens_database()
    {
        var services = new ServiceCollection();
        services.AddYatagarasu(options => options.DataDirectory = _dir);

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<GraphStore>();
        store.Write(write => write.CreateVertex("Person"));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose() { }
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
    {
        private readonly AsyncLocal<IReadOnlyList<KeyValuePair<string, object?>>?> _scope = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = _scope.Value;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                _scope.Value = values.ToArray();
            return new RestoreScope(_scope, previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            owner.Entries.Add(new LogEntry(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                _scope.Value ?? []));
        }

        private sealed class RestoreScope(
            AsyncLocal<IReadOnlyList<KeyValuePair<string, object?>>?> scope,
            IReadOnlyList<KeyValuePair<string, object?>>? previous) : IDisposable
        {
            public void Dispose() => scope.Value = previous;
        }
    }

    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> ScopeValues);
}

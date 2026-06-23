using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Quiver.Studio.Services;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string path, LogLevel minLevel = LogLevel.Debug)
    {
        _minLevel = minLevel;
        _writer = new StreamWriter(path, append: false, encoding: System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _writer, _minLevel));

    public void Dispose()
    {
        _writer.Dispose();
        _loggers.Clear();
    }
}

internal sealed class FileLogger(string category, StreamWriter writer, LogLevel minLevel) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRI",
            _ => "???"
        };

        var shortCategory = category.Length > 40
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {shortCategory}: {formatter(state, exception)}";
        lock (writer)
        {
            writer.WriteLine(line);
            if (exception is not null)
                writer.WriteLine(exception.ToString());
        }
    }
}

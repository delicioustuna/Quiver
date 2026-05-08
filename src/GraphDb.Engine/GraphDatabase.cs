using GraphDb.Engine.Transactions;
using Microsoft.Extensions.Logging;

namespace GraphDb.Engine;

public sealed class GraphDatabase : IDisposable
{
    private GraphDatabase() { }

    public static GraphDatabase Open(string directoryPath, GraphDatabaseOptions? options = null)
        => throw new NotImplementedException();

    public IGraphTransaction BeginTransaction(
        IsolationLevel level = IsolationLevel.SnapshotIsolation)
        => throw new NotImplementedException();

    public ISchemaApi Schema => throw new NotImplementedException();
    public IDiagnosticsApi Diagnostics => throw new NotImplementedException();

    public void Dispose() { }
}

public sealed class GraphDatabaseOptions
{
    public long BufferPoolSize { get; set; } = 256 * 1024 * 1024;
    public int WalSegmentSize { get; set; } = 64 * 1024 * 1024;
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public bool EnableChecksums { get; set; } = true;
    public ILoggerFactory? LoggerFactory { get; set; }
}

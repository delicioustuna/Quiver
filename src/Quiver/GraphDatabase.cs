using Quiver.Stores;
using Quiver.Transactions;
using Microsoft.Extensions.Logging;

namespace Quiver;

public sealed class GraphDatabase : IDisposable
{
    private readonly IGraphStorageBackend _backend;

    private GraphDatabase(IGraphStorageBackend backend)
    {
        _backend = backend;
    }

    public static GraphDatabase Open(string directoryPath, GraphDatabaseOptions? options = null)
    {
        options ??= new GraphDatabaseOptions();
        var factory = options.BackendFactory ?? CreateDefaultFactory(options.Backend);
        var backend = factory.Open(directoryPath, options);
        return new GraphDatabase(backend);
    }

    private static IGraphStorageBackendFactory CreateDefaultFactory(BackendKind kind) => kind switch
    {
        BackendKind.Binary => new BinaryGraphStorageBackendFactory(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown backend kind"),
    };

    /// <summary>
    /// Exposes the underlying backend. Intended for diagnostics and backend-specific
    /// capabilities; ordinary callers should prefer <see cref="BeginTransaction"/> etc.
    /// </summary>
    public IGraphStorageBackend Backend => _backend;

    /// <param name="buildAdjacencyIndex">
    /// When true, <see cref="BulkLoader.Commit"/> additionally builds adj.db + adj_idx.dat
    /// so the adjacency block store is available for subsequent read-only transactions.
    /// </param>
    public BulkLoader BeginBulkLoad(bool buildAdjacencyIndex = false)
    {
        var fn = _backend.BulkLoad.BeginBinaryBulkLoad
            ?? throw new NotSupportedException(
                "The active backend does not support binary bulk loading.");
        return fn(buildAdjacencyIndex);
    }

    public IGraphTransaction BeginTransaction(
        IsolationLevel level = IsolationLevel.SnapshotIsolation)
        => _backend.BeginGraphTransaction(level, readOnly: false);

    /// <summary>
    /// Opens a snapshot-isolation transaction marked as read-only.
    /// Read-only transactions are safe to use with parallel traversal operators
    /// such as <see cref="Quiver.Operators.ParallelBfsOperator"/>.
    /// </summary>
    public IGraphTransaction BeginReadOnlyTransaction()
        => _backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true);

    public ISchemaApi Schema => _backend.Schema;
    public IDiagnosticsApi Diagnostics => _backend.Diagnostics;

    /// <summary>
    /// Scan the entire database and return a fresh <see cref="GraphStats"/> snapshot.
    /// This is an O(N + E) operation and is typically called once at startup or after bulk loads.
    /// </summary>
    public GraphStats CollectStats()
    {
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return GraphStats.Collect(tx);
    }

    /// <summary>
    /// Create a <see cref="QueryOptimizer"/> backed by the given stats (or a freshly collected
    /// snapshot when <paramref name="stats"/> is null).
    /// </summary>
    public QueryOptimizer CreateOptimizer(GraphStats? stats = null)
        => new(stats ?? CollectStats());

    public void Dispose() => _backend.Dispose();
}

public sealed class GraphDatabaseOptions
{
    public long BufferPoolSize { get; set; } = 256 * 1024 * 1024;
    public int WalSegmentSize { get; set; } = 64 * 1024 * 1024;
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public bool EnableChecksums { get; set; } = true;
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Selects which built-in backend to instantiate when
    /// <see cref="BackendFactory"/> is null. Defaults to <see cref="BackendKind.Binary"/>.
    /// </summary>
    public BackendKind Backend { get; set; } = BackendKind.Binary;

    /// <summary>
    /// Optional explicit factory. When set, overrides <see cref="Backend"/>.
    /// Use this to inject custom (e.g. in-memory) backends from tests.
    /// </summary>
    public IGraphStorageBackendFactory? BackendFactory { get; set; }
}

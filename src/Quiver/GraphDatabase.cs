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
    /// VEC-5: the backend's vector store. Users call <c>CreateVectorIndex</c> /
    /// <c>SetVector</c> here directly; query-side access is via
    /// <c>g.Knn(...)</c> on the traversal source.
    /// </summary>
    public Core.IVectorStore Vectors => _backend.Vectors;

    /// <summary>
    /// Scan the entire database and return a fresh <see cref="GraphStats"/> snapshot.
    /// This is an O(N + E) operation and is typically called once at startup or after bulk loads.
    /// </summary>
    public GraphStats CollectStats()
        => CollectStats(GraphStats.PowerNodeDegreeThreshold);

    /// <summary>
    /// Variant that lets the caller override the power-node degree threshold used to
    /// populate <see cref="GraphStats.PowerNodes"/>. Useful in tests / diagnostics
    /// where the default <see cref="GraphStats.PowerNodeDegreeThreshold"/> is too large.
    /// </summary>
    public GraphStats CollectStats(int powerNodeThreshold)
    {
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return GraphStats.Collect(tx, powerNodeThreshold);
    }

    /// <summary>
    /// PW-16: overload that also lets callers tune the dense/sparse cut-over
    /// for the per-node degree lookup. See <see cref="NodeDegreeLookup"/>.
    /// </summary>
    public GraphStats CollectStats(int powerNodeThreshold, double denseThreshold)
    {
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return GraphStats.Collect(tx, powerNodeThreshold, denseThreshold);
    }

    /// <summary>
    /// Create a <see cref="QueryOptimizer"/> backed by the given stats (or a freshly collected
    /// snapshot when <paramref name="stats"/> is null).
    /// </summary>
    public QueryOptimizer CreateOptimizer(GraphStats? stats = null)
        => new(stats ?? CollectStats());

    /// <summary>
    /// PW-15 / codex_advice_3 §7.7. Build a point-in-time CSR/CSC snapshot of
    /// the current graph for repeated multi-pass algorithms (PageRank,
    /// Louvain, repeated BFS / shortest-path). Snapshot construction is
    /// O(N + E); subsequent neighbor lookups read flat arrays, which is
    /// cheaper than opening one adjacency cursor per node when an algorithm
    /// makes many passes over the same graph state.
    ///
    /// Internally opens a snapshot-isolation read-only transaction, builds
    /// the view, then closes the transaction — so the returned view does
    /// not pin a transaction beyond construction. Mutations after this call
    /// are invisible to the snapshot. Dispose the view to release pooled
    /// arrays.
    /// </summary>
    public IGraphSnapshotView OpenSnapshotView()
    {
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return GraphSnapshotView.Build(tx.Nodes, tx.Relationships, tx.AdjacencyBlocks);
    }

    /// <summary>
    /// FT-12 / codex_advice_3 §7.3. Build a SID-style join index from
    /// <see cref="RelationshipId"/> to the scalar value of
    /// <paramref name="propertyKey"/>. Intended for weighted traversals,
    /// edge filters, and algorithm kernels that already hold a
    /// relationship id and want the value without walking the property
    /// chain.
    ///
    /// O(R + P) build cost (one pass over the relationship store plus the
    /// per-rel property chain walk). Mutations after the build are
    /// invisible — rebuild after material graph changes when freshness
    /// matters. The returned index can outlive the build transaction.
    /// </summary>
    /// <param name="propertyKey">Property key name; must already exist via <see cref="ISchemaApi.GetOrCreatePropertyKey"/>.</param>
    /// <param name="expectedType">Scalar inline type to project. Values of other types are skipped.</param>
    public Stores.IRelationshipPropertyJoinIndex BuildRelationshipPropertyJoinIndex(
        string propertyKey,
        Stores.PropertyValueType expectedType)
    {
        ArgumentNullException.ThrowIfNull(propertyKey);
        var keyId = _backend.Schema.GetOrCreatePropertyKey(propertyKey);
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return Stores.DirectArrayRelationshipPropertyJoinIndex.Build(
            tx.Relationships, tx.Properties, keyId, expectedType);
    }

    /// <summary>
    /// PW-14 / codex_advice_3 §7.6. Rebuild the immutable base adjacency view
    /// from the current relationship state, drop tombstones, and advance the
    /// epoch. After this call all live edges are served from the base view and
    /// the delta walk becomes a no-op until new relationships are created.
    /// Throws when the active backend doesn't support compact (anything other
    /// than the binary backend without a payload lane). Caller must ensure no
    /// transactions are active.
    /// </summary>
    public void CompactAdjacency()
    {
        if (_backend is BinaryGraphStorageBackend binary)
            binary.CompactAdjacency();
        else
            throw new NotSupportedException(
                "CompactAdjacency is only implemented for the binary backend.");
    }

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

using GraphDb.Engine.Core;
using GraphDb.Engine.Index;
using GraphDb.Engine.Storage;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;
using GraphDb.Engine.Wal;
using Microsoft.Extensions.Logging;

namespace GraphDb.Engine;

public sealed class GraphDatabase : IDisposable
{
    private PageManager? _pageManager;
    private WriteAheadLog? _wal;
    private NodeStore? _nodeStore;
    private RelationshipStore? _relStore;
    private PropertyStore? _propStore;
    private LabelTokenStore? _labelTokens;
    private RelationshipTypeTokenStore? _relTypeTokens;
    private PropertyKeyTokenStore? _propKeyTokens;
    private IndexManager? _indexManager;
    private AdjacencyBlockStore? _adjStore;
    private ITransactionManager? _txManager;
    private ISchemaApi? _schema;
    private IDiagnosticsApi? _diagnostics;
    private string? _directoryPath;

    private GraphDatabase() { }

    public static GraphDatabase Open(string directoryPath, GraphDatabaseOptions? options = null)
    {
        options ??= new GraphDatabaseOptions();
        Directory.CreateDirectory(directoryPath);

        var db = new GraphDatabase();
        db._directoryPath = directoryPath;

        db._pageManager = new PageManager();

        var walDir = Path.Combine(directoryPath, "wal");
        db._wal = new WriteAheadLog(walDir, options.WalSegmentSize);

        var nodeFile = db._pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "nodes.db"), PageKind.Header);
        nodeFile.EnableWalLogging((byte)WalFileKind.Nodes);
        db._nodeStore = new NodeStore(nodeFile);

        var relFile = db._pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "rels.db"), PageKind.Header);
        relFile.EnableWalLogging((byte)WalFileKind.Relationships);
        db._relStore = new RelationshipStore(relFile);

        var propFile = db._pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "props.db"), PageKind.Header);
        propFile.EnableWalLogging((byte)WalFileKind.Properties);
        var blobFile = db._pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "blobs.db"), PageKind.Header);
        blobFile.EnableWalLogging((byte)WalFileKind.BlobData);
        db._propStore = new PropertyStore(propFile, blobFile);

        db._labelTokens    = new LabelTokenStore(Path.Combine(directoryPath, "labels.tok"));
        db._relTypeTokens  = new RelationshipTypeTokenStore(Path.Combine(directoryPath, "reltypes.tok"));
        db._propKeyTokens  = new PropertyKeyTokenStore(Path.Combine(directoryPath, "propkeys.tok"));

        var indexDir = Path.Combine(directoryPath, "indexes");
        db._indexManager = new IndexManager(indexDir);

        // Open adjacency block store if pre-built by BulkLoader.
        var adjDataPath = Path.Combine(directoryPath, "adj.db");
        var adjIndexPath = Path.Combine(directoryPath, "adj_idx.dat");
        if (File.Exists(adjDataPath) && File.Exists(adjIndexPath))
        {
            var adjFile = db._pageManager.OpenOrCreate(adjDataPath, PageKind.AdjacencyBlock);
            db._adjStore = new AdjacencyBlockStore(adjFile, adjIndexPath);
        }

        var fileRegistry = new Dictionary<byte, IPagedFile>
        {
            { (byte)WalFileKind.Nodes,         nodeFile },
            { (byte)WalFileKind.Relationships, relFile },
            { (byte)WalFileKind.Properties,    propFile },
            { (byte)WalFileKind.BlobData,      blobFile },
        };
        var recovery = new RecoveryManager(db._pageManager, db._wal, fileRegistry);
        recovery.Recover();

        db._txManager = new TransactionManager(
            db._wal, db._nodeStore, db._relStore, db._propStore, db._indexManager, db._adjStore);

        db._schema = new SchemaApi(db._labelTokens, db._relTypeTokens, db._propKeyTokens, db._indexManager);
        db._diagnostics = new DiagnosticsApi(db._nodeStore, db._relStore);

        return db;
    }

    /// <param name="buildAdjacencyIndex">
    /// When true, <see cref="BulkLoader.Commit"/> additionally builds adj.db + adj_idx.dat
    /// so the adjacency block store is available for subsequent read-only transactions.
    /// </param>
    public BulkLoader BeginBulkLoad(bool buildAdjacencyIndex = false)
        => new(_nodeStore!, _relStore!, _propStore!, buildAdjacencyIndex ? _directoryPath : null);

    public IGraphTransaction BeginTransaction(
        IsolationLevel level = IsolationLevel.SnapshotIsolation)
    {
        var inner = _txManager!.Begin(level);
        return new GraphTransaction(inner, _labelTokens!, _relTypeTokens!, _propKeyTokens!);
    }

    /// <summary>
    /// Opens a snapshot-isolation transaction marked as read-only.
    /// Read-only transactions are safe to use with parallel traversal operators
    /// such as <see cref="GraphDb.Engine.Operators.ParallelBfsOperator"/>.
    /// </summary>
    public IGraphTransaction BeginReadOnlyTransaction()
    {
        var inner = _txManager!.Begin(IsolationLevel.SnapshotIsolation);
        return new GraphTransaction(inner, _labelTokens!, _relTypeTokens!, _propKeyTokens!, isReadOnly: true);
    }

    public ISchemaApi Schema => _schema!;
    public IDiagnosticsApi Diagnostics => _diagnostics!;

    /// <summary>
    /// Scan the entire database and return a fresh <see cref="GraphStats"/> snapshot.
    /// This is an O(N + E) operation and is typically called once at startup or after bulk loads.
    /// </summary>
    public GraphStats CollectStats()
    {
        using var tx = _txManager!.Begin(IsolationLevel.SnapshotIsolation);
        return GraphStats.Collect(tx);
    }

    /// <summary>
    /// Create a <see cref="QueryOptimizer"/> backed by the given stats (or a freshly collected
    /// snapshot when <paramref name="stats"/> is null).
    /// </summary>
    public QueryOptimizer CreateOptimizer(GraphStats? stats = null)
        => new(stats ?? CollectStats());

    public void Dispose()
    {
        _txManager?.Dispose();
        _adjStore?.Dispose();
        _indexManager?.Dispose();
        _labelTokens?.Dispose();
        _relTypeTokens?.Dispose();
        _propKeyTokens?.Dispose();
        _wal?.Dispose();
        _pageManager?.Dispose();
    }
}

public sealed class GraphDatabaseOptions
{
    public long BufferPoolSize { get; set; } = 256 * 1024 * 1024;
    public int WalSegmentSize { get; set; } = 64 * 1024 * 1024;
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public bool EnableChecksums { get; set; } = true;
    public ILoggerFactory? LoggerFactory { get; set; }
}

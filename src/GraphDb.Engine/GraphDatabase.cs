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
    private ITransactionManager? _txManager;
    private ISchemaApi? _schema;
    private IDiagnosticsApi? _diagnostics;

    private GraphDatabase() { }

    public static GraphDatabase Open(string directoryPath, GraphDatabaseOptions? options = null)
    {
        options ??= new GraphDatabaseOptions();
        Directory.CreateDirectory(directoryPath);

        var db = new GraphDatabase();

        db._pageManager = new PageManager();

        var walDir = Path.Combine(directoryPath, "wal");
        db._wal = new WriteAheadLog(walDir, options.WalSegmentSize);

        var nodeFile = db._pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "nodes.db"), PageKind.Header);
        db._nodeStore = new NodeStore(nodeFile);

        var relFile = db._pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "rels.db"), PageKind.Header);
        db._relStore = new RelationshipStore(relFile);

        var propFile = db._pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "props.db"), PageKind.Header);
        var blobFile = db._pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "blobs.db"), PageKind.Header);
        db._propStore = new PropertyStore(propFile, blobFile);

        db._labelTokens    = new LabelTokenStore(Path.Combine(directoryPath, "labels.tok"));
        db._relTypeTokens  = new RelationshipTypeTokenStore(Path.Combine(directoryPath, "reltypes.tok"));
        db._propKeyTokens  = new PropertyKeyTokenStore(Path.Combine(directoryPath, "propkeys.tok"));

        var indexDir = Path.Combine(directoryPath, "indexes");
        db._indexManager = new IndexManager(indexDir);

        var recovery = new RecoveryManager(db._pageManager, db._wal);
        recovery.Recover();

        db._txManager = new TransactionManager(
            db._wal, db._nodeStore, db._relStore, db._propStore, db._indexManager);

        db._schema = new SchemaApi(db._labelTokens, db._relTypeTokens, db._propKeyTokens, db._indexManager);
        db._diagnostics = new DiagnosticsApi(db._nodeStore, db._relStore);

        return db;
    }

    public IGraphTransaction BeginTransaction(
        IsolationLevel level = IsolationLevel.SnapshotIsolation)
    {
        var inner = _txManager!.Begin(level);
        return new GraphTransaction(inner, _labelTokens!, _relTypeTokens!, _propKeyTokens!);
    }

    public ISchemaApi Schema => _schema!;
    public IDiagnosticsApi Diagnostics => _diagnostics!;

    public void Dispose()
    {
        _txManager?.Dispose();
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

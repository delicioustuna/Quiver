using Quiver.Core;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Stores;
using Quiver.Transactions;
using Quiver.Wal;

namespace Quiver;

internal sealed class BinaryGraphStorageBackend : IGraphStorageBackend
{
    private readonly IVectorStore _vectors;
    private readonly PageManager _pageManager;
    private readonly WriteAheadLog _wal;
    private readonly NodeStore _nodeStore;
    private readonly RelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    private readonly LabelTokenStore _labelTokens;
    private readonly RelationshipTypeTokenStore _relTypeTokens;
    private readonly PropertyKeyTokenStore _propKeyTokens;
    private readonly IndexManager _indexManager;
    // BA-6: holds either AdjacencyBlockStore (V1) or AdjacencyBlockStoreV2.
    // Disposed at backend teardown — the file lifetime is owned here even
    // though reads go through the interface only.
    private readonly IAdjacencyBlockStore? _adjStore;
    private readonly TransactionManager _txManager;
    private readonly SchemaApi _schema;
    private readonly DiagnosticsApi _diagnostics;
    private readonly IGraphAccessMethods _access;
    private readonly BulkLoadCapabilities _bulkLoad;
    private readonly string _directoryPath;

    internal BinaryGraphStorageBackend(
        string directoryPath,
        PageManager pageManager,
        WriteAheadLog wal,
        NodeStore nodeStore,
        RelationshipStore relStore,
        PropertyStore propStore,
        LabelTokenStore labelTokens,
        RelationshipTypeTokenStore relTypeTokens,
        PropertyKeyTokenStore propKeyTokens,
        IndexManager indexManager,
        IAdjacencyBlockStore? adjStore,
        TransactionManager txManager,
        BinaryGraphAccessMethods access,
        IVectorStore vectors)
    {
        _directoryPath = directoryPath;
        _vectors = vectors;
        _pageManager = pageManager;
        _wal = wal;
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _labelTokens = labelTokens;
        _relTypeTokens = relTypeTokens;
        _propKeyTokens = propKeyTokens;
        _indexManager = indexManager;
        _adjStore = adjStore;
        _txManager = txManager;

        _schema = new SchemaApi(_labelTokens, _relTypeTokens, _propKeyTokens, _indexManager);
        _diagnostics = new DiagnosticsApi(_nodeStore, _relStore, access);
        _access = access;
        _bulkLoad = new BulkLoadCapabilities
        {
            BeginBinaryBulkLoad = buildAdjacencyIndex => new BulkLoader(
                _nodeStore, _relStore, _propStore,
                buildAdjacencyIndex ? _directoryPath : null),
        };
    }

    public ITransactionManager Transactions => _txManager;
    public ISchemaApi Schema => _schema;
    public IDiagnosticsApi Diagnostics => _diagnostics;
    public IGraphAccessMethods Access => _access;
    public BulkLoadCapabilities BulkLoad => _bulkLoad;
    public IVectorStore Vectors => _vectors;

    public IGraphTransaction BeginGraphTransaction(IsolationLevel level, bool readOnly)
    {
        var inner = _txManager.Begin(level);
        return new GraphTransaction(inner, _labelTokens, _relTypeTokens, _propKeyTokens, readOnly);
    }

    public void Dispose()
    {
        _txManager.Dispose();
        if (_adjStore is IDisposable d) d.Dispose();
        _indexManager.Dispose();
        _labelTokens.Dispose();
        _relTypeTokens.Dispose();
        _propKeyTokens.Dispose();
        _wal.Dispose();
        _pageManager.Dispose();
    }
}

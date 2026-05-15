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
    // PW-14: mutable so CompactAdjacency can swap in a freshly rebuilt store.
    private IAdjacencyBlockStore? _adjStore;
    // PW-14: kept so CompactAdjacency can release the exclusive lock on
    // adj.db before AdjacencyBlockStore.Build reopens the path.
    private IPagedFile? _adjPagedFile;
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
        IPagedFile? adjPagedFile,
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
        _adjPagedFile = adjPagedFile;
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

    /// <summary>
    /// PW-14 / codex_advice_3 §7.6. Rebuild the immutable base adjacency view
    /// from the current relationship store, drop tombstones, and bump the
    /// epoch. After this call all live edges are served from base and the
    /// delta walk yields nothing until new relationships are created.
    ///
    /// Currently only supports the V1 store (no payload lane). When a V2
    /// store is active the call throws — V2 compact needs to re-read inline
    /// payloads from the property store and is deferred. Caller must ensure
    /// no transactions are active.
    /// </summary>
    public void CompactAdjacency()
    {
        if (_txManager.ActiveCount > 0)
            throw new InvalidOperationException(
                "CompactAdjacency requires no active transactions.");
        if (_adjStore is AdjacencyBlockStoreV2)
            throw new NotSupportedException(
                "CompactAdjacency for V2 (payload lane) is not yet implemented.");

        // Snapshot live rels (id, src, tgt, type) before tearing down the
        // current adj files — IRelationshipStore.Scan yields ids in store
        // order, and reading each pulls src/tgt/type from the active page.
        var live = new List<(long Id, long Src, long Tgt, int TypeId)>();
        long maxId = -1;
        foreach (var relId in _relStore.Scan())
        {
            var r = _relStore.Read(relId);
            live.Add((relId.Value, r.Source.Value, r.Target.Value, r.Type.Value));
            if (relId.Value > maxId) maxId = relId.Value;
        }
        long newBaseHwm = maxId + 1; // 0 when there are no rels — matches "no base"

        // Tear down the current store. The PagedFile holds an exclusive lock
        // on adj.db, so we must dispose AND drop it from the page manager
        // before AdjacencyBlockStore.Build reopens the path.
        var adjDataPath = Path.Combine(_directoryPath, "adj.db");
        var adjIndexPath = Path.Combine(_directoryPath, "adj_idx.dat");
        var adjEpochPath = Path.Combine(_directoryPath, "adj.epoch");

        if (_adjStore is AdjacencyBlockStore old) old.Dispose();
        _txManager.SwapAdjacencyStore(null);
        _adjStore = null;
        if (_adjPagedFile != null)
        {
            _pageManager.Drop(_adjPagedFile);
            _adjPagedFile.Dispose();
            _adjPagedFile = null;
        }

        // Rebuild the adjacency files in place. Build expects nodeHwm so that
        // the index has one entry per logical node id; use the highest src/tgt
        // we observed + 1, since this is the only signal we have post-bulk-load.
        long nodeHwm = 0;
        foreach (var (_, src, tgt, _) in live)
        {
            if (src + 1 > nodeHwm) nodeHwm = src + 1;
            if (tgt + 1 > nodeHwm) nodeHwm = tgt + 1;
        }
        AdjacencyBlockStore.Build(adjDataPath, adjIndexPath, live, nodeHwm);

        // Reset epoch metadata and reopen. ResetAfterCompact bumps the epoch
        // counter (so observers can detect the rebuild) and drops tombstones
        // since the new base view contains only live edges.
        AdjacencyEpoch newEpoch = File.Exists(adjEpochPath)
            ? AdjacencyEpoch.Load(adjEpochPath)
            : AdjacencyEpoch.CreateNew(adjEpochPath, 0);
        newEpoch.ResetAfterCompact(newBaseHwm);
        var newAdjFile = _pageManager.OpenOrCreate(adjDataPath, PageKind.AdjacencyBlock);
        var newStore = new AdjacencyBlockStore(newAdjFile, adjIndexPath, newEpoch);
        _adjStore = newStore;
        _adjPagedFile = newAdjFile;
        _txManager.SwapAdjacencyStore(newStore);
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

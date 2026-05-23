using Quiver.Core;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Stores;
using Quiver.Transactions;
using Quiver.Wal;

namespace Quiver;

/// <summary>
/// Default factory used when <see cref="GraphDatabaseOptions.Backend"/> is
/// <see cref="BackendKind.Binary"/>. Produces a <see cref="BinaryGraphStorageBackend"/>
/// constructed from the binary page / WAL / store / index components.
/// </summary>
public sealed class BinaryGraphStorageBackendFactory : IGraphStorageBackendFactory
{
    public IGraphStorageBackend Open(string directoryPath, GraphDatabaseOptions options)
    {
        // FT-15: a prior backend on this thread may have been killed mid-transaction
        // (crash simulation), leaving the thread-static WAL page context dangling.
        // Start clean — equivalent to the fresh thread-statics a real process restart
        // would have.
        // FT-20: IndexUndoContext was retired (subsumed by AbortUndoHandler), so no
        // index thread-static cleanup is needed here anymore.
        WalPageContext.End();

        Directory.CreateDirectory(directoryPath);

        var pageManager = new PageManager();

        var walDir = Path.Combine(directoryPath, "wal");
        var wal = new WriteAheadLog(walDir, options.WalSegmentSize);

        var nodeFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "nodes.db"), PageKind.Header);
        nodeFile.EnableWalLogging((byte)WalFileKind.Nodes, wal);
        var nodeStore = new NodeStore(nodeFile);

        var relFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "rels.db"), PageKind.Header);
        relFile.EnableWalLogging((byte)WalFileKind.Relationships, wal);
        var relStore = new RelationshipStore(relFile);

        var propFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "props.db"), PageKind.Header);
        propFile.EnableWalLogging((byte)WalFileKind.Properties, wal);
        var blobFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "blobs.db"), PageKind.Header);
        blobFile.EnableWalLogging((byte)WalFileKind.BlobData, wal);
        var propStore = new PropertyStore(propFile, blobFile);

        var labelTokens   = new LabelTokenStore(Path.Combine(directoryPath, "labels.tok"));
        var relTypeTokens = new RelationshipTypeTokenStore(Path.Combine(directoryPath, "reltypes.tok"));
        var propKeyTokens = new PropertyKeyTokenStore(Path.Combine(directoryPath, "propkeys.tok"));

        var indexDir = Path.Combine(directoryPath, "indexes");
        // FT-19: IndexManager に WAL + runtime fileRegistry を渡し、新規索引作成時に
        // PagedFile を EnableWalLogging で配線して fileRegistry に登録する経路を貫通させる。
        // fileRegistry は下で data files を追加した後、indexManager.MaterializeAll で既存索引も
        // 追加し、recovery が透過的に全 file kind を扱えるようにする。
        var fileRegistry = new Dictionary<byte, IPagedFile>
        {
            { (byte)WalFileKind.Nodes,         nodeFile },
            { (byte)WalFileKind.Relationships, relFile },
            { (byte)WalFileKind.Properties,    propFile },
            { (byte)WalFileKind.BlobData,      blobFile },
        };
        var indexManager = new IndexManager(indexDir, wal, fileRegistry);
        // FT-19: 既存索引を recovery 前に open + EnableWalLogging + fileRegistry へ登録。
        // これがないと recovery の PageImage / CLR replay が索引ファイルを引けず redo が失敗する。
        indexManager.MaterializeAll(fileRegistry);

        // PW-14: epoch metadata (base relationship hwm + tombstones) is shared
        // by both V1 and V2 stores. Created by BulkLoader on initial build and
        // updated in-place on tombstone / compact.
        var adjEpochPath = Path.Combine(directoryPath, "adj.epoch");
        AdjacencyEpoch? adjEpoch = File.Exists(adjEpochPath) ? AdjacencyEpoch.Load(adjEpochPath) : null;

        // BA-6: prefer V2 (with payload lane) when present, otherwise V1.
        IAdjacencyBlockStore? adjStore = null;
        IPagedFile? adjPagedFile = null;
        var adjV2DataPath = Path.Combine(directoryPath, "adj_v2.db");
        var adjV2IndexPath = Path.Combine(directoryPath, "adj_v2_idx.dat");
        var adjV2MetaPath = Path.Combine(directoryPath, "adj_v2.meta");
        if (File.Exists(adjV2DataPath) && File.Exists(adjV2IndexPath) && File.Exists(adjV2MetaPath))
        {
            var spec = AdjacencyBlockStoreV2.ReadMeta(adjV2MetaPath);
            adjPagedFile = pageManager.OpenOrCreate(adjV2DataPath, PageKind.AdjacencyBlock);
            adjStore = new AdjacencyBlockStoreV2(adjPagedFile, adjV2IndexPath, spec, adjEpoch);
        }
        else
        {
            var adjDataPath = Path.Combine(directoryPath, "adj.db");
            var adjIndexPath = Path.Combine(directoryPath, "adj_idx.dat");
            if (File.Exists(adjDataPath) && File.Exists(adjIndexPath))
            {
                adjPagedFile = pageManager.OpenOrCreate(adjDataPath, PageKind.AdjacencyBlock);
                adjStore = new AdjacencyBlockStore(adjPagedFile, adjIndexPath, adjEpoch);
            }
        }

        // FT-19: fileRegistry には data file + materialize 済み索引が既に登録されている。
        // 索引も ARIES page-WAL 対象なので PageImage redo + CLR undo が透過的に走る。
        var recovery = new RecoveryManager(pageManager, wal, fileRegistry);
        recovery.Recover();

        // FT-15: store metadata (hwm / freeHead / inUseCount) is page-backed; the
        // constructors above read it from the on-disk header pages BEFORE recovery
        // ran. After redo / undo rewrote those header pages, re-sync the in-memory
        // caches so they reflect the recovered state.
        void ReloadStoreMeta()
        {
            nodeStore.ReloadMeta();
            relStore.ReloadMeta();
            propStore.ReloadMeta();
        }
        ReloadStoreMeta();

        var vectors = new InMemoryVectorStore();
        var access = new BinaryGraphAccessMethods(vectors);

        // VEC-11: in-memory inverted index keyed by LabelId so label-filtered
        // scans run in O(|L|) instead of O(N). Built lazily from a single
        // NodeStore.Scan() after WAL recovery; subsequent Allocate/Free are
        // notified by the store via the LabelNodeIndex hook, and bulk loads
        // invalidate (next lookup triggers rebuild).
        var labelIndex = new LabelNodeIndex();
        nodeStore.AttachLabelIndex(labelIndex);
        access.AttachLabelIndex(labelIndex);

        // FT-15: in-process undo handler — restores captured before-images and
        // re-syncs store metadata on abort / commit failure.
        var undoHandler = new AbortUndoHandler(fileRegistry, ReloadStoreMeta);

        var txManager = new TransactionManager(
            wal, nodeStore, relStore, propStore, indexManager, adjStore, access,
            undoHandler);

        // 案A: チェックポイント契機を配線する。コミットごとに WAL 成長量を見て、
        // しきい値超過 + アクティブ TX 0 の時点で全データページを flush し WAL を truncate する。
        // FT-18: IndexManager も渡し、checkpoint 時に索引ファイルも一緒に fsync する。
        // これが無いと WAL truncate 後にコミット済み索引エントリが恒久消失する。
        var checkpointer = new Checkpointer(
            pageManager, wal, () => txManager.OldestActiveLsn, indexManager);
        txManager.EnableCheckpointing(checkpointer, options.CheckpointThresholdBytes);

        return new BinaryGraphStorageBackend(
            directoryPath, pageManager, wal, nodeStore, relStore, propStore,
            labelTokens, relTypeTokens, propKeyTokens, indexManager,
            adjStore, adjPagedFile, txManager, access, vectors,
            options.LogicalMutationSink);
    }
}

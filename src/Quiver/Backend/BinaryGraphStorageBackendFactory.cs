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
        Directory.CreateDirectory(directoryPath);

        var pageManager = new PageManager();

        var walDir = Path.Combine(directoryPath, "wal");
        var wal = new WriteAheadLog(walDir, options.WalSegmentSize);

        var nodeFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "nodes.db"), PageKind.Header);
        nodeFile.EnableWalLogging((byte)WalFileKind.Nodes);
        var nodeStore = new NodeStore(nodeFile);

        var relFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "rels.db"), PageKind.Header);
        relFile.EnableWalLogging((byte)WalFileKind.Relationships);
        var relStore = new RelationshipStore(relFile);

        var propFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "props.db"), PageKind.Header);
        propFile.EnableWalLogging((byte)WalFileKind.Properties);
        var blobFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "blobs.db"), PageKind.Header);
        blobFile.EnableWalLogging((byte)WalFileKind.BlobData);
        var propStore = new PropertyStore(propFile, blobFile);

        var labelTokens   = new LabelTokenStore(Path.Combine(directoryPath, "labels.tok"));
        var relTypeTokens = new RelationshipTypeTokenStore(Path.Combine(directoryPath, "reltypes.tok"));
        var propKeyTokens = new PropertyKeyTokenStore(Path.Combine(directoryPath, "propkeys.tok"));

        var indexDir = Path.Combine(directoryPath, "indexes");
        var indexManager = new IndexManager(indexDir);

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

        var fileRegistry = new Dictionary<byte, IPagedFile>
        {
            { (byte)WalFileKind.Nodes,         nodeFile },
            { (byte)WalFileKind.Relationships, relFile },
            { (byte)WalFileKind.Properties,    propFile },
            { (byte)WalFileKind.BlobData,      blobFile },
        };
        var recovery = new RecoveryManager(pageManager, wal, fileRegistry);
        recovery.Recover();

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

        var txManager = new TransactionManager(
            wal, nodeStore, relStore, propStore, indexManager, adjStore, access);

        // 案A: チェックポイント契機を配線する。コミットごとに WAL 成長量を見て、
        // しきい値超過 + アクティブ TX 0 の時点で全データページを flush し WAL を truncate する。
        var checkpointer = new Checkpointer(pageManager, wal, () => txManager.OldestActiveLsn);
        txManager.EnableCheckpointing(checkpointer, options.CheckpointThresholdBytes);

        return new BinaryGraphStorageBackend(
            directoryPath, pageManager, wal, nodeStore, relStore, propStore,
            labelTokens, relTypeTokens, propKeyTokens, indexManager,
            adjStore, adjPagedFile, txManager, access, vectors,
            options.LogicalMutationSink);
    }
}

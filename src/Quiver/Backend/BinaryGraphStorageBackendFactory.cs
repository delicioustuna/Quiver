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

        AdjacencyBlockStore? adjStore = null;
        var adjDataPath = Path.Combine(directoryPath, "adj.db");
        var adjIndexPath = Path.Combine(directoryPath, "adj_idx.dat");
        if (File.Exists(adjDataPath) && File.Exists(adjIndexPath))
        {
            var adjFile = pageManager.OpenOrCreate(adjDataPath, PageKind.AdjacencyBlock);
            adjStore = new AdjacencyBlockStore(adjFile, adjIndexPath);
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

        var access = new BinaryGraphAccessMethods();
        var txManager = new TransactionManager(
            wal, nodeStore, relStore, propStore, indexManager, adjStore, access);

        return new BinaryGraphStorageBackend(
            directoryPath, pageManager, wal, nodeStore, relStore, propStore,
            labelTokens, relTypeTokens, propKeyTokens, indexManager, adjStore, txManager, access);
    }
}

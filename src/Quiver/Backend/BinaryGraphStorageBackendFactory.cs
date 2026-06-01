using Quiver.Core;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Quiver.Storage.Wal;

namespace Quiver;

/// <summary>
/// Default factory used when <see cref="GraphDatabaseOptions.Backend"/> is
/// <see cref="BackendKind.Binary"/>. Produces a <see cref="BinaryGraphStorageBackend"/>
/// constructed from the binary page / WAL / store / index components.
/// </summary>
internal sealed class BinaryGraphStorageBackendFactory : IGraphStorageBackendFactory
{
    public IGraphStorageBackend Open(string directoryPath, GraphDatabaseOptions options)
    {
        // FT-15: a prior backend on this thread may have been killed mid-transaction
        // (crash simulation), leaving the thread-static WAL page context dangling.
        // Start clean — equivalent to the fresh thread-statics a real process restart
        // would have.
        // FT-20: IndexUndoContext was retired (subsumed by AbortUndoHandler), so no
        // index thread-static cleanup is needed here anymore.
        // FT-26: MvccContext も同様にスレッドローカルなので念のため初期化。
        WalPageContext.End();
        MvccContext.End();

        Directory.CreateDirectory(directoryPath);

        var pageManager = new PageManager();

        var walDir = Path.Combine(directoryPath, "wal");
        var wal = new WriteAheadLog(walDir, options.WalSegmentSize, options.GroupCommitWindow);

        // FT-32: MVCC sidecar — record から撤去した xmin/xmax を EntityKind 別 sidecar に持つ。
        // 各 sidecar PagedFile も EnableWalLogging で同一 WAL に連動させ、データレコードと
        // 同一トランザクションで PageImage / before-image が記録される (commit / abort / crash で整合)。
        var nodeFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "nodes.db"), PageKind.Header);
        nodeFile.EnableWalLogging((byte)WalFileKind.Nodes, wal);
        var nodeVerFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "nodes.ver"), PageKind.Header);
        nodeVerFile.EnableWalLogging((byte)WalFileKind.NodeVersionMeta, wal);
        var nodeVersions = new EntityVersionStore(nodeVerFile);
        var nodeStore = new NodeStore(nodeFile, labelIndex: null, nodeVersions);

        var relFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "rels.db"), PageKind.Header);
        relFile.EnableWalLogging((byte)WalFileKind.Relationships, wal);
        var relVerFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "rels.ver"), PageKind.Header);
        relVerFile.EnableWalLogging((byte)WalFileKind.RelationshipVersionMeta, wal);
        var relVersions = new EntityVersionStore(relVerFile);
        var relStore = new RelationshipStore(relFile, relVersions);

        var propFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "props.db"), PageKind.Header);
        propFile.EnableWalLogging((byte)WalFileKind.Properties, wal);
        var blobFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "blobs.db"), PageKind.Header);
        blobFile.EnableWalLogging((byte)WalFileKind.BlobData, wal);
        var propVerFile = pageManager.OpenOrCreate(
            Path.Combine(directoryPath, "props.ver"), PageKind.Header);
        propVerFile.EnableWalLogging((byte)WalFileKind.PropertyVersionMeta, wal);
        var propVersions = new EntityVersionStore(propVerFile);
        var propStore = new PropertyStore(propFile, blobFile, propVersions);

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
            // FT-32: sidecar も ARIES page-WAL 対象。recovery の PageImage redo / abort の
            // before-image undo がデータレコードと一貫して走るよう registry に登録する。
            { (byte)WalFileKind.NodeVersionMeta,         nodeVerFile },
            { (byte)WalFileKind.RelationshipVersionMeta, relVerFile },
            { (byte)WalFileKind.PropertyVersionMeta,     propVerFile },
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

        // FT-26: MVCC visibility 判定用の committed TxId 集合。recovery が WAL を走査して
        // (Commit レコードがあり、かつ Abort も無く、PageImage を持つ等の信頼できる条件を満たす)
        // tx を Mark してから TransactionManager 配線へ。Bootstrap は ctor で自動登録される。
        var committedRegistry = new CommittedTxRegistry();

        // FT-19: fileRegistry には data file + materialize 済み索引が既に登録されている。
        // 索引も ARIES page-WAL 対象なので PageImage redo + CLR undo が透過的に走る。
        var recovery = new RecoveryManager(pageManager, wal, fileRegistry, committedRegistry: committedRegistry);
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
            undoHandler, options.LockingMode, options.LockTimeout,
            options.DeadlockDetectionInterval, committedRegistry,
            // FT-33: SSN (Serializable) は version sidecar の Pstamp/Sstamp を使う。
            nodeVersions, relVersions);
        // FT-26: recovery で観測した最大 TxId より大きい値から新規 tx を採番するよう、
        // TransactionManager の _nextTxId を巻き上げる。これがないと新規 tx ID が
        // 過去 commit 済み TxId と衝突して registry が同じ entry を 2 回 Mark してしまう。
        txManager.AdvanceNextTxIdAtLeast(committedRegistry.MaxObservedTxId + 1);
        // FT-33 (④): recovery 後の node sidecar ヘッダから SSN commit-stamp 高水位を読み、
        // クロックをそこまで巻き上げる。これがないと再起動でクロックが 0 に戻り、永続化済みの
        // 旧 stamp 空間と新 stamp 空間が混在して Serializable tx が過剰 abort する。
        txManager.SeedCommitStamp(nodeVersions.ReadCommitStampHighWater());

        // 案A: チェックポイント契機を配線する。コミットごとに WAL 成長量を見て、
        // しきい値超過 + アクティブ TX 0 の時点で全データページを flush し WAL を truncate する。
        // FT-18: IndexManager も渡し、checkpoint 時に索引ファイルも一緒に fsync する。
        // これが無いと WAL truncate 後にコミット済み索引エントリが恒久消失する。
        var checkpointer = new Checkpointer(
            pageManager, wal, () => txManager.OldestActiveLsn, indexManager);
        txManager.EnableCheckpointing(checkpointer, options.CheckpointThresholdBytes);
        // FT-28: Adaptive ポリシー時は controller を作成して TxManager に注入。
        // controller は warmup 完了までは options.CheckpointThresholdBytes (initial) を返す。
        if (options.CheckpointPolicy == Quiver.Transactions.CheckpointPolicy.Adaptive
            && options.CheckpointThresholdBytes > 0)
        {
            var adaptive = new AdaptiveCheckpointController(
                options.CheckpointThresholdBytes,
                options.TargetRecoveryTime,
                options.MinCheckpointThresholdBytes,
                options.MaxCheckpointThresholdBytes,
                options.AdaptiveSampleWindow);
            txManager.SetAdaptiveController(adaptive);
        }

        var backend = new BinaryGraphStorageBackend(
            directoryPath, pageManager, wal, nodeStore, relStore, propStore,
            labelTokens, relTypeTokens, propKeyTokens, indexManager,
            adjStore, adjPagedFile, txManager, access, vectors,
            labelIndex,
            options.LogicalMutationSink,
            options.TargetRecoveryTime,
            options.MinCheckpointThresholdBytes,
            options.MaxCheckpointThresholdBytes,
            options.AdaptiveSampleWindow);

        // FT-22: recovery 直後に opt-in で orphan を掃除する。recovery が tornw write 等で
        // 「base data の delete commit が durable / 索引 delete が未到達」だった場合に発生する
        // orphan entry を運用者の介入なしに除去する。default false。
        if (options.AutoRepairOrphansOnRecovery)
            backend.Diagnostics.RepairIndexes(IndexRepairMode.Apply);

        return backend;
    }
}

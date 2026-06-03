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
    // ARCH-4: コンテナ内の全コアページを載せる単一 WAL fileKind。旧 per-store WalFileKind
    // (Nodes=1..PropertyVersionMeta=7) とも索引予約レンジ (0x40+) とも衝突しない値を使う。
    // 特に vacuum の WriteFileTruncate は WalFileKind.Nodes 等を渡すため、DataFileKind がそれらと
    // 衝突すると recovery の FileTruncate replay が container.Physical 全体を誤って物理 truncate する。
    private const byte DataFileKind = 0x20;

    // ARCH-4: カタログ内のテナント ID (WAL fileKind とは別空間。各 store / sidecar / token に 1 つ)。
    private const byte TenantNodes = 1;
    private const byte TenantRels = 2;
    private const byte TenantProps = 3;
    private const byte TenantBlobs = 4;
    private const byte TenantNodeVer = 5;
    private const byte TenantRelVer = 6;
    private const byte TenantPropVer = 7;
    private const byte TenantLabelTok = 8;
    private const byte TenantRelTypeTok = 9;
    private const byte TenantPropKeyTok = 10;

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

        // ARCH-4 増分7: WAL は wal/ セグメント群ではなく単一サイドカー graph.quiver-wal。
        // クリーン終了で削除され、静止時は graph.quiver のみが残る。
        var walPath = Path.Combine(directoryPath, "graph.quiver-wal");
        var wal = new WriteAheadLog(walPath, options.WalSegmentSize, options.GroupCommitWindow);

        // ARCH-4: 単一ファイルコンテナ。コア store / version sidecar / token を 1 つの
        // graph.quiver に同居させ、全ページを単一 DATA fileKind で WAL に載せる (option B)。
        // 物理ページ ID は全テナント横断で一意なので recovery / abort は純物理ページ単位で動く。
        // 索引は引き続き予約レンジ (0x40+) の別ファイル (増分4 で吸収予定)。
        // GraphDatabaseOptions.BufferPoolSize を共有プール容量に実配線する。
        int poolPages = (int)Math.Max(64, options.BufferPoolSize / PagedFile.PageSizeConst);
        var container = new SingleFileContainer(
            Path.Combine(directoryPath, "graph.quiver"), poolPages);
        container.EnableWalLogging(DataFileKind, wal);
        // checkpoint (pageManager.FlushAll) / snapshot 経路に container 物理ファイルを乗せる。
        pageManager.Adopt(container.Physical);

        // ARCH-4: テナント (= 各 store) は recovery 完了後に open する。kill 後の reopen では
        // カタログ / page-table の content が未フラッシュで失われうるため、recovery が物理 page1
        // (カタログ) と page-table ページを WAL から復元してから OpenTenant しないと、空カタログを
        // 見て tenant を再生成し、WAL の物理ページ ID と乖離して committed データを取りこぼす。
        // ARCH-4 増分5: コア store / sidecar / token / 索引はすべて単一 DATA fileKind =
        // container.Physical 上のテナント。索引も物理ページとして同居するので、recovery / abort は
        // 索引も含めて純物理ページ単位で透過的に動く (索引専用 fileKind / MaterializeAll は不要)。
        var fileRegistry = new Dictionary<byte, IPagedFile>
        {
            { DataFileKind, container.Physical },
        };

        // FT-26: MVCC visibility 判定用の committed TxId 集合。recovery が WAL を走査して
        // (Commit レコードがあり、かつ Abort も無く、PageImage を持つ等の信頼できる条件を満たす)
        // tx を Mark してから TransactionManager 配線へ。Bootstrap は ctor で自動登録される。
        var committedRegistry = new CommittedTxRegistry();

        // FT-19: fileRegistry には data file + materialize 済み索引が既に登録されている。
        // 索引も ARIES page-WAL 対象なので PageImage redo + CLR undo が透過的に走る。
        var recovery = new RecoveryManager(pageManager, wal, fileRegistry, committedRegistry: committedRegistry);
        recovery.Recover();

        // ARCH-4: recovery が物理 page1 (カタログ) + page-table + header ページを WAL から復元した。
        // ここで container の in-memory カタログを正本へ読み直してから、テナントを open する。
        container.ReloadAll();

        // ARCH-4 増分5: 索引マネージャは recovery + ReloadAll の後に構築する。索引カタログ
        // テナントと各索引テナントの page-table は物理ページとして recovery 済みなので、
        // ここで container から開き直すだけで永続済み索引を materialize できる。
        var indexManager = new IndexManager(container);

        // ARCH-4 増分6: 隣接ビュー (bulk load 済みのときのみ存在) を container テナントから開く。
        // PW-14 epoch (base hwm + tombstones) も EpochTenant に同居。V1/V2 種別は DataTenant の
        // 記述子で判別する。recovery + ReloadAll 後なのでテナントページは復元済み。
        IAdjacencyBlockStore? adjStore = null;
        AdjacencyEpoch? adjEpoch = null;
        if (container.HasTenant(AdjacencyContainer.DataTenant))
        {
            var adjData = container.OpenTenant(AdjacencyContainer.DataTenant, PageKind.AdjacencyBlock);
            var (adjKind, adjSpec) = AdjacencyContainer.ReadDescriptor(adjData);
            if (adjKind != AdjacencyContainer.KindNone)
            {
                var adjIdx = container.OpenTenant(AdjacencyContainer.IndexTenant, PageKind.Header);
                adjEpoch = AdjacencyEpoch.Open(container.OpenTenant(AdjacencyContainer.EpochTenant, PageKind.Header));
                adjStore = adjKind == AdjacencyContainer.KindV2
                    ? new AdjacencyBlockStoreV2(adjData, adjIdx, adjSpec!.Value, adjEpoch)
                    : new AdjacencyBlockStore(adjData, adjIdx, adjEpoch);
            }
        }

        var nodeFile = container.OpenTenant(TenantNodes, PageKind.Header);
        var nodeVerFile = container.OpenTenant(TenantNodeVer, PageKind.Header);
        var nodeVersions = new EntityVersionStore(nodeVerFile);
        var nodeStore = new NodeStore(nodeFile, labelIndex: null, nodeVersions);

        var relFile = container.OpenTenant(TenantRels, PageKind.Header);
        var relVerFile = container.OpenTenant(TenantRelVer, PageKind.Header);
        var relVersions = new EntityVersionStore(relVerFile);
        var relStore = new RelationshipStore(relFile, relVersions);

        var propFile = container.OpenTenant(TenantProps, PageKind.Header);
        var blobFile = container.OpenTenant(TenantBlobs, PageKind.Header);
        var propVerFile = container.OpenTenant(TenantPropVer, PageKind.Header);
        var propVersions = new EntityVersionStore(propVerFile);
        var propStore = new PropertyStore(propFile, blobFile, propVersions);

        var labelTokens   = new LabelTokenStore(container.OpenTenant(TenantLabelTok, PageKind.TokenRecord));
        var relTypeTokens = new RelationshipTypeTokenStore(container.OpenTenant(TenantRelTypeTok, PageKind.TokenRecord));
        var propKeyTokens = new PropertyKeyTokenStore(container.OpenTenant(TenantPropKeyTok, PageKind.TokenRecord));

        // FT-15 / ARCH-4: abort (CLR undo) 後に container のテナント記述子 / page table と store メタを
        // 再同期するコールバック。AbortUndoHandler が before-image 復元後に呼ぶ。
        void ReloadStoreMeta()
        {
            container.ReloadAll();
            nodeStore.ReloadMeta();
            relStore.ReloadMeta();
            propStore.ReloadMeta();
            // ARCH-4: トークンページもコンテナの WAL 対象なので、abort で CLR がディスクを
            // tx 開始前へ戻す。in-memory 辞書も読み直してディスクと一致させないと、後続 commit が
            // 「メモリにあるがディスクに無い」トークンの再永続化をスキップし reopen で消える。
            labelTokens.Reload();
            relTypeTokens.Reload();
            propKeyTokens.Reload();
            // ARCH-4 増分6: epoch テナントも container WAL 対象。abort で CLR がページを戻すので
            // in-memory の epoch / baseRelHwm / tombstone を読み直してディスクと一致させる。
            adjEpoch?.Reload();
        }

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

        // ARCH-4 増分7: クリーン終了で WAL が削除されていた場合、recovery では committedRegistry が
        // 空のままになる (WAL から復元できない)。container に永続化された committed TxId 高水位から
        // visibility horizon (= これ未満は presumed-committed) と次 TxId 採番起点を復元する。
        // abort 済み tx の効果は CLR で巻き戻り済みなので、高水位未満を一律 committed と presume しても
        // 生存レコードはすべて committed tx の xmin を持ち、安全。crash 経路では WAL 由来の horizon が
        // より新しいため max を取る。
        long containerHighWater = container.CommittedHighWaterTxId;
        if (containerHighWater > 0)
        {
            if (containerHighWater - 1 > committedRegistry.RecoveryHorizon)
                committedRegistry.RecoveryHorizon = containerHighWater - 1;
            txManager.AdvanceNextTxIdAtLeast(containerHighWater);
        }

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
            directoryPath, container, pageManager, wal, nodeStore, relStore, propStore,
            labelTokens, relTypeTokens, propKeyTokens, indexManager,
            adjStore, txManager, access, vectors,
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

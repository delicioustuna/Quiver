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
    // コンテナ内の全コアページを載せる単一 WAL fileKind。旧 per-store WalFileKind
    // (Nodes=1..PropertyVersionMeta=7) とも索引予約レンジ (0x40+) とも衝突しない値を使う。
    // 特に vacuum の WriteFileTruncate は WalFileKind.Nodes 等を渡すため、DataFileKind がそれらと
    // 衝突すると recovery の FileTruncate replay が container.Physical 全体を誤って物理 truncate する。
    private const byte DataFileKind = 0x20;

    // カタログ内のテナント ID (WAL fileKind とは別空間。各 store / sidecar / token に 1 つ)。
    internal const byte TenantNodes = 1;
    private const byte TenantRels = 2;
    private const byte TenantProps = 3;
    private const byte TenantBlobs = 4;
    private const byte TenantNodeVer = 5;
    private const byte TenantRelVer = 6;
    private const byte TenantPropVer = 7;
    private const byte TenantLabelTok = 8;
    private const byte TenantRelTypeTok = 9;
    private const byte TenantPropKeyTok = 10;
    // VersionedNodeStore の ItemPointerMap (Sequence→物理位置) テナント。
    // 11/12/13 は AdjacencyContainer (DataTenant/IndexTenant/EpochTenant) が使用済みのため 14。
    internal const byte TenantNodeMap = 14;
    // VersionedRelationshipStore の ItemPointerMap テナント。
    private const byte TenantRelMap = 15;
    // opt-in 列の catalog テナント (各列テナントは ColumnCatalog が 64+ で採番)。
    private const byte TenantColumnCatalog = 16;
    // 永続ベクトルインデックスの catalog テナント (各 index の payload/HNSW テナントは
    // VectorIndexCatalog が 200+ で採番)。
    private const byte TenantVectorCatalog = 17;
    // 第一級ハイパーエッジ。18..24 は固定 tenant で、後続 store 実装でも変更しない。
    internal const byte TenantHyperedgeHeap = 18;
    internal const byte TenantHyperedgeMap = 19;
    internal const byte TenantHyperedgeVersion = 20;
    // incidence は fixed-slot 直接アドレスの単一テナント (ヘッダページ + slot ページ)。
    // 間接マップを持たないため tenant 22 は使わない。番号は詰め直さず欠番のまま残し、
    // 既存 DB の他テナント番号を動かさない。
    internal const byte TenantIncidenceHeap = 21;
    // 22 は旧 incidence 間接マップの欠番。再割り当てしない。
    internal const byte TenantHyperedgeTypeToken = 23;
    internal const byte TenantRoleToken = 24;
    // node sequence 直引きの 6B incidence head sidecar 用 tenant。
    internal const byte TenantNodeIncidenceHead = 25;

    public IGraphStorageBackend Open(string filePath, GraphDatabaseOptions options)
    {
        // filePath は単一コンテナ (*.quiver) のフルパス。親ディレクトリを用意する。
        var parentDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

        var pageManager = new PageManager();

        // WAL は単一サイドカー <filePath>-wal。クリーン終了で削除され、
        // 静止時は *.quiver のみが残る。
        var walPath = filePath + "-wal";
        var wal = new WriteAheadLog(walPath, options.WalSegmentSize, options.GroupCommitWindow);

        // 単一ファイルコンテナ。コア store / version sidecar / token / 索引 / 隣接ブロック /
        // epoch をすべて *.quiver に同居させ、全ページを単一 DATA fileKind で WAL に載せる。
        // 物理ページ ID は全テナント横断で一意なので recovery / abort は純物理ページ単位で動く。
        // GraphDatabaseOptions.BufferPoolSize を共有プール容量に実配線する。
        int poolPages = (int)Math.Max(64, options.BufferPoolSize / PagedFile.PageSizeConst);
        var container = new SingleFileContainer(filePath, poolPages);
        return OpenCore(filePath, options, pageManager, wal, container, recover: true);
    }

    /// <summary>
    /// RAM 専用の物理ページ層と WAL を使い、通常バックエンドと同じストア群を組み立てる。
    /// </summary>
    internal IGraphStorageBackend OpenInMemory(GraphDatabaseOptions options)
    {
        var pageManager = new PageManager();
        var wal = new NullWriteAheadLog();
        var container = new SingleFileContainer(new InMemoryPagedFile());
        var backend = OpenCore(string.Empty, options, pageManager, wal, container, recover: false);
        return new InMemoryGraphStorageBackend((BinaryGraphStorageBackend)backend);
    }

    private static IGraphStorageBackend OpenCore(
        string filePath,
        GraphDatabaseOptions options,
        PageManager pageManager,
        IWriteAheadLog wal,
        SingleFileContainer container,
        bool recover)
    {
        // 同スレッドの先行 backend がトランザクション途中で終了している可能性がある
        // (crash シミュレーション等)。スレッドローカルなコンテキストをクリーン状態へ戻す。
        WalPageContext.End();
        MvccContext.End();

        container.EnableWalLogging(DataFileKind, wal);
        // checkpoint (pageManager.FlushAll) / snapshot 経路に container 物理ファイルを乗せる。
        pageManager.Adopt(container.Physical);

        // テナント (= 各 store) は recovery 完了後に open する。kill 後の reopen では
        // カタログ / page-table の content が未フラッシュで失われうるため、recovery が物理 page1
        // (カタログ) と page-table ページを WAL から復元してから OpenTenant しないと、空カタログを
        // 見て tenant を再生成し、WAL の物理ページ ID と乖離して committed データを取りこぼす。
        // コア store / sidecar / token / 索引はすべて単一 DATA fileKind =
        // container.Physical 上のテナント。索引も物理ページとして同居するので、recovery / abort は
        // 索引も含めて純物理ページ単位で透過的に動く (索引専用 fileKind / MaterializeAll は不要)。
        var fileRegistry = new Dictionary<byte, IPagedFile>
        {
            { DataFileKind, container.Physical },
        };

        // MVCC visibility 判定用の committed TxId 集合。recovery が WAL を走査して
        // (Commit レコードがあり、かつ Abort も無く、PageImage を持つ等の信頼できる条件を満たす)
        // tx を Mark してから TransactionManager 配線へ。Bootstrap は ctor で自動登録される。
        var committedRegistry = new CommittedTxRegistry();

        // fileRegistry には data file + materialize 済み索引が既に登録されている。
        // 索引も ARIES page-WAL 対象なので PageImage redo + CLR undo が透過的に走る。
        RecoveryManager? recovery = null;
        if (recover)
        {
            recovery = new RecoveryManager(
                pageManager, wal, fileRegistry, committedRegistry: committedRegistry);
            recovery.Recover();

            // recovery が物理 page1 (カタログ) + page-table + header ページを WAL から復元した。
            // ここで container の in-memory カタログを正本へ読み直してから、テナントを open する。
            container.ReloadAll();
        }

        // 索引マネージャは recovery + ReloadAll の後に構築する。索引カタログ
        // テナントと各索引テナントの page-table は物理ページとして recovery 済みなので、
        // ここで container から開き直すだけで永続済み索引を materialize できる。
        var indexManager = new IndexManager(container);

        // recovery 論理相。物理相 (recovery.Recover 上) が FtStructureImage で
        // FT 木の構造を復元済みで、IndexManager が 2a 後のヘッダから live FullTextIndex を構築した今、
        // committed tx の FtLeafMutation を再実行 (2b) + loser tx の逆操作 undo (Pass 3) を適用する。
        recovery?.RecoverLogical(indexManager);

        // 隣接ビュー (bulk load 済みのときのみ存在) を container テナントから開く。
        // epoch (base hwm + tombstones) も EpochTenant に同居。V1/V2 種別は DataTenant の
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

        // ノードは slotted ヒープ (TenantNodes) + ItemPointerMap (TenantNodeMap) に
        // 載る。MVCC/Generation/SSN は従来どおり sidecar (TenantNodeVer) で管理する。
        var nodeFile = container.OpenTenant(TenantNodes, PageKind.Header);
        var nodeMapFile = container.OpenTenant(TenantNodeMap, PageKind.Header);
        var nodeVerFile = container.OpenTenant(TenantNodeVer, PageKind.Header);
        var nodeVersions = new EntityVersionStore(nodeVerFile);
        var nodeMap = new ItemPointerMap(nodeMapFile);
        var nodeStore = new VersionedNodeStore(nodeFile, nodeMap, labelIndex: null, nodeVersions);

        // リレーションシップも slotted ヒープ (TenantRels) + ItemPointerMap
        // (TenantRelMap) に載る。MVCC は heap version、Generation/SSN は sidecar (TenantRelVer)。
        var relFile = container.OpenTenant(TenantRels, PageKind.Header);
        var relMapFile = container.OpenTenant(TenantRelMap, PageKind.Header);
        var relVerFile = container.OpenTenant(TenantRelVer, PageKind.Header);
        var relVersions = new EntityVersionStore(relVerFile);
        var relMap = new ItemPointerMap(relMapFile);
        var relStore = new VersionedRelationshipStore(relFile, relMap, relVersions);

        var propFile = container.OpenTenant(TenantProps, PageKind.Header);
        var blobFile = container.OpenTenant(TenantBlobs, PageKind.Header);
        var propVerFile = container.OpenTenant(TenantPropVer, PageKind.Header);
        var propVersions = new EntityVersionStore(propVerFile);
        var propStore = new PropertyStore(propFile, blobFile, propVersions);

        var labelTokens   = new LabelTokenStore(container.OpenTenant(TenantLabelTok, PageKind.TokenRecord));
        var relTypeTokens = new RelationshipTypeTokenStore(container.OpenTenant(TenantRelTypeTok, PageKind.TokenRecord));
        var propKeyTokens = new PropertyKeyTokenStore(container.OpenTenant(TenantPropKeyTok, PageKind.TokenRecord));

        var hyperedgeHeapFile = container.OpenTenant(TenantHyperedgeHeap, PageKind.Header);
        var hyperedgeMapFile = container.OpenTenant(TenantHyperedgeMap, PageKind.Header);
        var hyperedgeVerFile = container.OpenTenant(TenantHyperedgeVersion, PageKind.Header);
        var hyperedgeVersions = new EntityVersionStore(hyperedgeVerFile);
        var hyperedgeMap = new ItemPointerMap(hyperedgeMapFile);
        var hyperedgeStore = new VersionedHyperedgeStore(hyperedgeHeapFile, hyperedgeMap, hyperedgeVersions);

        var incidenceHeapFile = container.OpenTenant(TenantIncidenceHeap, PageKind.Header);
        var incidenceStore = new IncidenceStore(incidenceHeapFile);

        var nodeIncidenceHeadFile = container.OpenTenant(TenantNodeIncidenceHead, PageKind.Header);
        var nodeIncidenceHeadStore = new NodeIncidenceHeadStore(nodeIncidenceHeadFile);

        var hyperedgeTypeTokens = new HyperedgeTypeTokenStore(
            container.OpenTenant(TenantHyperedgeTypeToken, PageKind.TokenRecord));
        var roleTokens = new RoleTokenStore(
            container.OpenTenant(TenantRoleToken, PageKind.TokenRecord));

        // 列マネージャを startup で eager に開く。
        // 登録済み列の head cache を開いておくことで (1) write 経路が列を維持でき、
        // (2) abort の ReloadStoreMeta から列 cache を head ページへ再同期できる。
        var columnManager = new ColumnManager(
            container, TenantColumnCatalog, relStore, nodeStore, propStore);

        // ベクトル payload を container テナントへ永続化するストア。InMemoryVectorStore を置換し、
        // 再起動を跨いで KNN を再現する。書き込みは container WAL に乗るので tx 配下なら原子整合する。
        // (kind, seq) → 現世代 resolver を渡し、slot 再利用で化けた stale binding を弾く。
        var vectors = new PersistentVectorStore(container, TenantVectorCatalog,
            (kind, seq) => kind switch
            {
                Core.EntityKind.Node => nodeStore.CurrentGeneration(seq),
                Core.EntityKind.Relationship => relStore.CurrentGeneration(seq),
                Core.EntityKind.Hyperedge => hyperedgeStore.CurrentGeneration(seq),
                _ => -1,
            },
            options.VectorCacheBudgetBytes);

        // abort (CLR undo) 後に container のテナント記述子 / page table と store メタを
        // 再同期するコールバック。AbortUndoHandler が before-image 復元後に呼ぶ。
        void ReloadStoreMeta()
        {
            container.ReloadAll();
            nodeStore.ReloadMeta();
            relStore.ReloadMeta();
            hyperedgeStore.ReloadMeta();
            incidenceStore.ReloadMeta();
            propStore.ReloadMeta();
            labelTokens.Reload();
            relTypeTokens.Reload();
            propKeyTokens.Reload();
            hyperedgeTypeTokens.Reload();
            roleTokens.Reload();
            // epoch テナントも container WAL 対象。abort で CLR がページを戻すので
            // in-memory の epoch / baseRelHwm / tombstone を読み直してディスクと一致させる。
            adjEpoch?.Reload();
            // 列 head ページも container WAL 対象。abort の before-image undo で
            // head ページが tx 開始前へ戻るので、列の in-memory cache をページから再構築して
            // head 値の正当性を回復する。delta の中止 tx 分は OnRolledBack の PruneAbortedTx で掃除。
            columnManager.ReloadColumns();
            // ベクトル payload / catalog ページも container WAL 対象。abort の before-image
            // undo でページが tx 開始前へ戻るので、in-memory の catalog / payload meta を読み直す。
            vectors.ReloadAll();
            // B+Tree 索引 (secondary + 全文 postings/norms) の in-memory ヘッダキャッシュ
            // (root / entryCount / height) も abort で戻ったページから読み直す。これが無いと
            // EntryCount が陳腐化し、索引 split を含む tx の abort で root/height が不整合になる。
            indexManager.ReloadAll();
        }

        var access = new BinaryGraphAccessMethods(vectors);

        // LabelId をキーとする in-memory 転置索引。ラベル付き scan が O(N) ではなく O(|L|) で走る。
        // WAL recovery 後の NodeStore.Scan() から遅延構築し、Allocate/Free は store の
        // LabelNodeIndex hook で通知され、bulk load は無効化 (次回 lookup で再構築)。
        var labelIndex = new LabelNodeIndex();
        nodeStore.AttachLabelIndex(labelIndex);
        access.AttachLabelIndex(labelIndex);

        // in-process undo handler。abort / commit 失敗時に before-image を復元し store メタを再同期する。
        // leaf 論理 undo の適用器も渡す (postings/norms の Suppressed leaf を
        // abort 時に逆操作で取り消す)。
        var undoHandler = new AbortUndoHandler(fileRegistry, ReloadStoreMeta,
            (tenant, isUpsert, key, value) => indexManager.ApplyFtLeafUndo(tenant, isUpsert, key, value));

        var txManager = new TransactionManager(
            wal, nodeStore, relStore, propStore, indexManager, adjStore, access,
            undoHandler, options.LockingMode, options.LockTimeout,
            options.DeadlockDetectionInterval, committedRegistry,
            nodeVersions, relVersions,
            hyperedgeStore, incidenceStore, nodeIncidenceHeadStore, hyperedgeVersions);
        // recovery で観測した最大 TxId より大きい値から新規 tx を採番するよう、
        // TransactionManager の _nextTxId を巻き上げる。これがないと新規 tx ID が
        // 過去 commit 済み TxId と衝突して registry が同じ entry を 2 回 Mark してしまう。
        txManager.AdvanceNextTxIdAtLeast(committedRegistry.MaxObservedTxId + 1);
        // recovery 後の node sidecar ヘッダから SSN commit-stamp 高水位を読み、
        // クロックをそこまで巻き上げる。これがないと再起動でクロックが 0 に戻り、永続化済みの
        // 旧 stamp 空間と新 stamp 空間が混在して Serializable tx が過剰 abort する。
        txManager.SeedCommitStamp(nodeVersions.ReadCommitStampHighWater());

        // クリーン終了で WAL が削除されていた場合、recovery では committedRegistry が
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

        // チェックポイント契機を配線する。コミットごとに WAL 成長量を見て、
        // しきい値超過 + アクティブ TX 0 の時点で全データページを flush し WAL を truncate する。
        // IndexManager も渡し、checkpoint 時に索引ファイルも一緒に fsync する。
        // これが無いと WAL truncate 後にコミット済み索引エントリが恒久消失する。
        if (recover)
        {
            var checkpointer = new Checkpointer(
                pageManager, wal, () => txManager.OldestActiveLsn, indexManager);
            txManager.EnableCheckpointing(checkpointer, options.CheckpointThresholdBytes);
            // Adaptive ポリシー時は controller を作成して TxManager に注入。
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
        }

        var backend = new BinaryGraphStorageBackend(
            filePath, container, pageManager, wal, nodeStore, relStore, propStore,
            labelTokens, relTypeTokens, propKeyTokens, hyperedgeTypeTokens, roleTokens, indexManager,
            adjStore, txManager, access, vectors,
            columnManager,
            labelIndex,
            options.LogicalMutationSink,
            options.TargetRecoveryTime,
            options.MinCheckpointThresholdBytes,
            options.MaxCheckpointThresholdBytes,
            options.AdaptiveSampleWindow);

        // recovery 直後に opt-in で orphan を掃除する。recovery が torn write 等で
        // 「base data の delete commit が durable / 索引 delete が未到達」だった場合に発生する
        // orphan entry を運用者の介入なしに除去する。default false。
        if (options.AutoRepairOrphansOnRecovery)
            backend.Diagnostics.RepairIndexes(IndexRepairMode.Apply);

        return backend;
    }
}

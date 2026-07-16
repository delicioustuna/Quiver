using Quiver.Core;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Quiver.Storage.Wal;

namespace Quiver;

/// <summary>
/// Default factory used when <see cref="QuiverDatabaseOptions.Backend"/> is
/// <see cref="BackendKind.Binary"/>. Produces a <see cref="BinaryGraphStorageBackend"/>
/// constructed from the binary page / WAL / store / index components.
/// </summary>
internal sealed class BinaryGraphStorageBackendFactory : IGraphStorageBackendFactory
{
    // コンテナ内の全コアページを載せる単一 WAL fileKind。旧 per-store WalFileKind
    // (Vertices=1..EdgeVersionMeta=6) とも索引予約レンジ (0x40+) とも衝突しない値を使う。
    // 特に vacuum の WriteFileTruncate は WalFileKind.Vertices 等を渡すため、DataFileKind がそれらと
    // 衝突すると recovery の FileTruncate replay が container.Physical 全体を誤って物理 truncate する。
    private const byte DataFileKind = 0x20;

    // カタログ内のテナント ID (WAL fileKind とは別空間。各 store / sidecar / token に 1 つ)。
    internal const byte TenantVertices = 1;
    private const byte TenantEdges = 2;
    private const byte TenantProps = 3;
    private const byte TenantBlobs = 4;
    private const byte TenantVertexVer = 5;
    private const byte TenantEdgeVer = 6;
    // 7 は property entity sidecar を削除した clean-break 後の欠番。
    private const byte TenantLabelTok = 8;
    private const byte TenantEdgeTypeTok = 9;
    private const byte TenantPropKeyTok = 10;
    // VersionedVertexStore の ItemPointerMap (Sequence→物理位置) テナント。
    // 11/12/13 は AdjacencyContainer (DataTenant/IndexTenant/EpochTenant) が使用済みのため 14。
    internal const byte TenantVertexMap = 14;
    // VersionedEdgeStore の ItemPointerMap テナント。
    private const byte TenantEdgeMap = 15;
    // opt-in 列の catalog テナント (各列テナントは ColumnCatalog が 64+ で採番)。
    private const byte TenantColumnCatalog = 16;
    // 永続ベクトルインデックスの catalog テナント (各 index の payload/HNSW テナントは
    // VectorIndexCatalog が 200+ で採番)。
    private const byte TenantVectorCatalog = 17;
    // 第一級Nexus。18..24 は固定 tenant で、後続 store 実装でも変更しない。
    internal const byte TenantNexusHeap = 18;
    internal const byte TenantNexusMap = 19;
    internal const byte TenantNexusVersion = 20;
    // incidence は fixed-slot 直接アドレスの単一テナント (ヘッダページ + slot ページ)。
    // 間接マップを持たないため tenant 22 は使わない。番号は詰め直さず欠番のまま残し、
    // 既存 DB の他テナント番号を動かさない。
    internal const byte TenantIncidenceHeap = 21;
    // 22 は旧 incidence 間接マップの欠番。再割り当てしない。
    internal const byte TenantNexusTypeToken = 23;
    internal const byte TenantRoleToken = 24;
    // vertex sequence 直引きの 6B incidence head sidecar 用 tenant。
    internal const byte TenantVertexIncidenceHead = 25;
    internal const byte TenantEdgeLocator = 26;
    internal const byte TenantEdgeDeltaHead = 27;
    internal const byte TenantEdgeDeltaPages = 28;
    internal const byte TenantVectorPayloadMetadata = 29;
    internal const byte TenantVectorPayloadBlobs = 30;

    public IGraphStorageBackend Open(string filePath, QuiverDatabaseOptions options)
    {
        // filePath は単一コンテナ (*.quiver) のフルパス。親ディレクトリを用意する。
        var parentDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

        var pageManager = new PageManager();

        // WAL は単一サイドカー <filePath>-wal。クリーン終了で削除され、
        // 静止時は *.quiver のみが残る。
        var walPath = filePath + "-wal";
        var wal = new WriteAheadLog(walPath, options.GroupCommitWindow);

        // 単一ファイルコンテナ。コア store / version sidecar / token / 索引 / 隣接ブロック /
        // epoch をすべて *.quiver に同居させ、全ページを単一 DATA fileKind で WAL に載せる。
        // 物理ページ ID は全テナント横断で一意なので recovery / abort は純物理ページ単位で動く。
        // QuiverDatabaseOptions.BufferPoolSize を共有プール容量に実配線する。
        int poolPages = (int)Math.Max(64, options.BufferPoolSize / PagedFile.PageSizeConst);
        var container = new SingleFileContainer(
            filePath,
            poolPages,
            options.InitialFileAllocationBytes,
            options.MaximumFileGrowthStepBytes);
        return OpenCore(filePath, options, pageManager, wal, container, recover: true);
    }

    /// <summary>
    /// RAM 専用の物理ページ層と WAL を使い、通常バックエンドと同じストア群を組み立てる。
    /// </summary>
    internal IGraphStorageBackend OpenInMemory(QuiverDatabaseOptions options)
    {
        var pageManager = new PageManager();
        var wal = new NullWriteAheadLog();
        var container = new SingleFileContainer(new InMemoryPagedFile());
        var backend = OpenCore(string.Empty, options, pageManager, wal, container, recover: false);
        return new InMemoryGraphStorageBackend((BinaryGraphStorageBackend)backend);
    }

    private static IGraphStorageBackend OpenCore(
        string filePath,
        QuiverDatabaseOptions options,
        PageManager pageManager,
        IWriteAheadLog wal,
        SingleFileContainer container,
        bool recover)
    {
        // 同スレッドの先行 backend がトランザクション途中で終了している可能性がある
        // (crash シミュレーション等)。ambient コンテキストをクリーン状態へ戻す。
        WalWriteSetContext.End();
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
        // 索引も page-WAL 対象なので、明示 Commit を持つトランザクションだけを redo する。
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

        // 隣接ビュー (bulk load 済みのときのみ存在) を container テナントから開く。
        // epoch (base hwm + tombstones) も EpochTenant に同居する。
        IAdjacencySegmentStore? adjStore = null;
        AdjacencyEpoch? adjEpoch = null;
        if (container.HasTenant(AdjacencyContainer.DataTenant))
        {
            var adjData = container.OpenTenant(AdjacencyContainer.DataTenant, PageKind.AdjacencyBlock);
            var (adjKind, adjSpec) = AdjacencyContainer.ReadDescriptor(adjData);
            if (adjKind == AdjacencyContainer.KindSegment && adjSpec is { } spec)
            {
                var adjIdx = container.OpenTenant(AdjacencyContainer.IndexTenant, PageKind.Header);
                adjEpoch = AdjacencyEpoch.Open(container.OpenTenant(AdjacencyContainer.EpochTenant, PageKind.Header));
                adjStore = new AdjacencySegmentStore(adjData, adjIdx, spec, adjEpoch);
            }
        }

        // Vertexは slotted ヒープ (TenantVertices) + ItemPointerMap (TenantVertexMap) に
        // 載る。MVCC/Generation/SSN は従来どおり sidecar (TenantVertexVer) で管理する。
        var vertexFile = container.OpenTenant(TenantVertices, PageKind.Header);
        var vertexMapFile = container.OpenTenant(TenantVertexMap, PageKind.Header);
        var vertexVerFile = container.OpenTenant(TenantVertexVer, PageKind.Header);
        var vertexVersions = new EntityVersionStore(vertexVerFile);
        var vertexMap = new ItemPointerMap(vertexMapFile);
        var vertexStore = new VersionedVertexStore(vertexFile, vertexMap, labelIndex: null, vertexVersions);

        // Edgeも slotted ヒープ (TenantEdges) + ItemPointerMap
        // (TenantEdgeMap) に載る。MVCC は heap version、Generation/SSN は sidecar (TenantEdgeVer)。
        var edgeFile = container.OpenTenant(TenantEdges, PageKind.Header);
        var edgeMapFile = container.OpenTenant(TenantEdgeMap, PageKind.Header);
        var relVerFile = container.OpenTenant(TenantEdgeVer, PageKind.Header);
        var relLocatorFile = container.OpenTenant(TenantEdgeLocator, PageKind.Header);
        var edgeVersions = new EntityVersionStore(relVerFile);
        var edgeMap = new ItemPointerMap(edgeMapFile);
        var edgeLocators = new EdgeLocatorStore(relLocatorFile);
        var edgeStore = new VersionedEdgeStore(edgeFile, edgeMap, edgeVersions, edgeLocators);
        var edgeDeltaHeads = new EdgeDeltaHeadStore(
            container.OpenTenant(TenantEdgeDeltaHead, PageKind.Header));
        var edgeDeltas = new PersistentEdgeDeltaStore(
            container.OpenTenant(TenantEdgeDeltaPages, PageKind.EdgeDeltaRecord),
            edgeDeltaHeads);

        var propFile = container.OpenTenant(TenantProps, PageKind.Header);
        var blobFile = container.OpenTenant(TenantBlobs, PageKind.Header);
        var vectorPayloadMetadataFile = container.OpenTenant(
            TenantVectorPayloadMetadata, PageKind.Header);
        var vectorPayloadBlobFile = container.OpenTenant(
            TenantVectorPayloadBlobs, PageKind.Header);
        var propStore = new PropertyVersionStore(
            propFile,
            blobFile,
            vectorPayloadMetadataFile,
            vectorPayloadBlobFile);

        var labelTokens   = new LabelTokenStore(container.OpenTenant(TenantLabelTok, PageKind.TokenRecord));
        var edgeTypeTokens = new EdgeTypeTokenStore(container.OpenTenant(TenantEdgeTypeTok, PageKind.TokenRecord));
        var propKeyTokens = new PropertyKeyTokenStore(container.OpenTenant(TenantPropKeyTok, PageKind.TokenRecord));

        var nexusHeapFile = container.OpenTenant(TenantNexusHeap, PageKind.Header);
        var nexusMapFile = container.OpenTenant(TenantNexusMap, PageKind.Header);
        var nexusVerFile = container.OpenTenant(TenantNexusVersion, PageKind.Header);
        var nexusVersions = new EntityVersionStore(nexusVerFile);
        var nexusMap = new ItemPointerMap(nexusMapFile);
        var nexusStore = new VersionedNexusStore(nexusHeapFile, nexusMap, nexusVersions);

        var incidenceHeapFile = container.OpenTenant(TenantIncidenceHeap, PageKind.Header);
        var incidenceStore = new IncidenceStore(incidenceHeapFile);

        var vertexIncidenceHeadFile = container.OpenTenant(TenantVertexIncidenceHead, PageKind.Header);
        var vertexIncidenceHeadStore = new VertexIncidenceHeadStore(vertexIncidenceHeadFile);

        var nexusTypeTokens = new NexusTypeTokenStore(
            container.OpenTenant(TenantNexusTypeToken, PageKind.TokenRecord));
        var roleTokens = new RoleTokenStore(
            container.OpenTenant(TenantRoleToken, PageKind.TokenRecord));

        CoMembershipBlockStore? coMembershipStore = null;
        if (options.CoMembershipRolePairs.Count > 0)
        {
            var resolvedPairs = new HashSet<(RoleId OriginRole, RoleId MemberRole)>();
            foreach (CoMembershipRolePair pair in options.CoMembershipRolePairs)
            {
                if (string.IsNullOrWhiteSpace(pair.OriginRole))
                    throw new ArgumentException(
                        "Co-membership origin role must not be null, empty, or whitespace.",
                        nameof(options));
                if (string.IsNullOrWhiteSpace(pair.MemberRole))
                    throw new ArgumentException(
                        "Co-membership member role must not be null, empty, or whitespace.",
                        nameof(options));
                resolvedPairs.Add((
                    roleTokens.GetOrCreate(pair.OriginRole),
                    roleTokens.GetOrCreate(pair.MemberRole)));
            }

            coMembershipStore = new CoMembershipBlockStore(resolvedPairs);
            // 導出ビューは recovery 済みの正本だけから作る。途中でプロセスが停止しても
            // 次回 open で同じ再構築を行うため、独自の WAL や永続レイアウトを持たない。
            coMembershipStore.Rebuild(nexusStore, incidenceStore);
        }

        // 列マネージャを startup で eager に開く。
        // 登録済み列の head cache を開いておくことで (1) write 経路が列を維持でき、
        // (2) abort の ReloadStoreMeta から列 cache を head ページへ再同期できる。
        var columnManager = new ColumnManager(
            container, TenantColumnCatalog, edgeStore, vertexStore, nexusStore, propStore);

        // ベクトル payload を container テナントへ永続化するストア。InMemoryVectorStore を置換し、
        // 再起動を跨いで KNN を再現する。書き込みは container WAL に乗るので tx 配下なら原子整合する。
        // (kind, seq) → 現世代 resolver を渡し、slot 再利用で化けた stale binding を弾く。
        var vectors = new PersistentVectorStore(container, TenantVectorCatalog,
            (kind, seq) => kind switch
            {
                Core.EntityKind.Vertex => vertexStore.CurrentGeneration(seq),
                Core.EntityKind.Edge => edgeStore.CurrentGeneration(seq),
                Core.EntityKind.Nexus => nexusStore.CurrentGeneration(seq),
                _ => -1,
            },
            options.VectorCacheBudgetBytes);

        // abort の before-image 復元後に container のテナント記述子 / page table と store メタを
        // 再同期するコールバック。AbortUndoHandler が before-image 復元後に呼ぶ。
        void ReloadStoreMeta()
        {
            container.ReloadAll();
            vertexStore.ReloadMeta();
            edgeStore.ReloadMeta();
            edgeDeltaHeads.ReloadMeta();
            edgeDeltas.ReloadMeta();
            nexusStore.ReloadMeta();
            incidenceStore.ReloadMeta();
            propStore.ReloadMeta();
            labelTokens.Reload();
            edgeTypeTokens.Reload();
            propKeyTokens.Reload();
            nexusTypeTokens.Reload();
            roleTokens.Reload();
            // epoch テナントも container WAL 対象。abort で before-image がページを戻すので
            // in-memory の epoch / baseEdgeHwm / tombstone を読み直してディスクと一致させる。
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

        var access = new BinaryGraphAccessMethods(
            vectors,
            new EdgeDeltaStore(edgeDeltas));

        // LabelId をキーとする in-memory 転置索引。ラベル付き scan が O(N) ではなく O(|L|) で走る。
        // WAL recovery 後の VertexStore.Scan() から遅延構築し、Allocate/Free は store の
        // LabelVertexIndex hook で通知され、bulk load は無効化 (次回 lookup で再構築)。
        var labelIndex = new LabelVertexIndex();
        vertexStore.AttachLabelIndex(labelIndex);
        access.AttachLabelIndex(labelIndex);

        // in-process undo handler。abort / commit 失敗時に before-image を復元し store メタを再同期する。
        var undoHandler = new AbortUndoHandler(fileRegistry, ReloadStoreMeta);

        var txManager = new TransactionManager(
            wal, vertexStore, edgeStore, propStore, indexManager, adjStore, access,
            undoHandler, options.LockingMode, options.LockTimeout,
            options.DeadlockDetectionInterval, committedRegistry,
            vertexVersions, edgeVersions,
            nexusStore, incidenceStore, vertexIncidenceHeadStore, nexusVersions,
            coMembershipStore,
            edgeDeltas);
        // recovery で観測した最大 TxId より大きい値から新規 tx を採番するよう、
        // TransactionManager の _nextTxId を巻き上げる。これがないと新規 tx ID が
        // 過去 commit 済み TxId と衝突して registry が同じ entry を 2 回 Mark してしまう。
        txManager.AdvanceNextTxIdAtLeast(committedRegistry.MaxObservedTxId + 1);
        // recovery 後の vertex sidecar ヘッダから SSN commit-stamp 高水位を読み、
        // クロックをそこまで巻き上げる。これがないと再起動でクロックが 0 に戻り、永続化済みの
        // 旧 stamp 空間と新 stamp 空間が混在して Serializable tx が過剰 abort する。
        txManager.SeedCommitStamp(vertexVersions.ReadCommitStampHighWater());

        // クリーン終了で WAL が削除されていた場合、recovery では committedRegistry が
        // 空のままになる (WAL から復元できない)。container に永続化された committed TxId 高水位から
        // visibility horizon (= これ未満は presumed-committed) と次 TxId 採番起点を復元する。
        // abort 済み tx の効果は before-image で巻き戻り済みなので、高水位未満を committed とみなしても
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
            filePath, container, pageManager, wal, vertexStore, edgeStore, propStore,
            labelTokens, edgeTypeTokens, propKeyTokens, nexusTypeTokens, roleTokens, indexManager,
            adjStore, txManager, access, vectors,
            columnManager,
            coMembershipStore,
            labelIndex,
            edgeDeltaHeads,
            edgeDeltas,
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

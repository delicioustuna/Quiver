using Quiver.Core;
using Quiver.Index;
using Quiver.Logical;
using Quiver.Maintenance;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Quiver.Storage.Wal;

namespace Quiver;

internal sealed class BinaryGraphStorageBackend : IGraphStorageBackendInternal
{
    private readonly IVectorStore _vectors;
    // db.Vectors の公開面。tx 外のミューテーションを autocommit tx で包む
    // (tx 内の呼び出しは ambient WalPageContext を検出して join する)。生の _vectors は
    // access methods / tx 配下 SetVector の委譲先として内部で使い続ける。
    private IVectorStore? _vectorsFacade;
    private readonly PageManager _pageManager;
    private readonly IWriteAheadLog _wal;
    private readonly VersionedNodeStore _nodeStore;
    private readonly VersionedRelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    private readonly LabelTokenStore _labelTokens;
    private readonly RelationshipTypeTokenStore _relTypeTokens;
    private readonly PropertyKeyTokenStore _propKeyTokens;
    private readonly HyperedgeTypeTokenStore _hyperedgeTypeTokens;
    private readonly RoleTokenStore _roleTokens;
    private readonly IndexManager _indexManager;
    // AdjacencyBlockStore (V1) または AdjacencyBlockStoreV2 を保持。
    // CompactAdjacency が再構築したストアを差し替えるため mutable。
    // 隣接データは container 内テナントに同居するため、別 PagedFile の所有は不要。
    private IAdjacencyBlockStore? _adjStore;
    // bulk load / CompactAdjacency が隣接テナントを構築するために保持する。
    private readonly SingleFileContainer _container;
    private readonly TransactionManager _txManager;
    private readonly SchemaApi _schema;
    private readonly DiagnosticsApi _diagnostics;
    private readonly IGraphAccessMethods _access;
    private readonly BulkLoadCapabilities _bulkLoad;
    // 単一コンテナ (*.quiver) のフルパス。WAL サイドカー = _containerPath + "-wal"。
    private readonly string _containerPath;
    private readonly ILogicalMutationSink? _logicalSink;

    internal BinaryGraphStorageBackend(
        string containerPath,
        SingleFileContainer container,
        PageManager pageManager,
        IWriteAheadLog wal,
        VersionedNodeStore nodeStore,
        VersionedRelationshipStore relStore,
        PropertyStore propStore,
        LabelTokenStore labelTokens,
        RelationshipTypeTokenStore relTypeTokens,
        PropertyKeyTokenStore propKeyTokens,
        HyperedgeTypeTokenStore hyperedgeTypeTokens,
        RoleTokenStore roleTokens,
        IndexManager indexManager,
        IAdjacencyBlockStore? adjStore,
        TransactionManager txManager,
        BinaryGraphAccessMethods access,
        IVectorStore vectors,
        ColumnManager columnManager,
        LabelNodeIndex? labelIndex = null,
        ILogicalMutationSink? logicalSink = null,
        TimeSpan? adaptiveTargetRecoveryTime = null,
        long adaptiveMinThresholdBytes = 4L * 1024 * 1024,
        long adaptiveMaxThresholdBytes = 1024L * 1024 * 1024,
        int adaptiveSampleWindow = 1000)
    {
        _logicalSink = logicalSink;
        _containerPath = containerPath;
        _container = container;
        _vectors = vectors;
        _pageManager = pageManager;
        _wal = wal;
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _labelTokens = labelTokens;
        _relTypeTokens = relTypeTokens;
        _propKeyTokens = propKeyTokens;
        _hyperedgeTypeTokens = hyperedgeTypeTokens;
        _roleTokens = roleTokens;
        _indexManager = indexManager;
        _adjStore = adjStore;
        _txManager = txManager;
        _columnManager = columnManager;

        _schema = new SchemaApi(_labelTokens, _relTypeTokens, _propKeyTokens, _indexManager,
            _hyperedgeTypeTokens, _roleTokens);
        // index manager と label index を DiagnosticsApi に渡して
        // CheckIndexConsistency / RepairIndexes が機能するようにする。
        // TransactionManager を渡し、CurrentCheckpointThresholdBytes /
        // SetCheckpointPolicy をホットスワップ経路として公開する。Adaptive 用パラメタは
        // factory で既知の options 値を持つので、後段で AttachAdaptiveDefaults により上書き可能。
        _diagnostics = new DiagnosticsApi(
            _nodeStore, _relStore, access, _indexManager, labelIndex, _txManager,
            adaptiveTargetRecoveryTime,
            adaptiveMinThresholdBytes,
            adaptiveMaxThresholdBytes,
            adaptiveSampleWindow);
        _access = access;
        _bulkLoad = new BulkLoadCapabilities
        {
            BeginBinaryBulkLoad = buildAdjacencyIndex => new BulkLoader(
                _nodeStore, _relStore, _propStore,
                buildAdjacencyIndex ? _container : null),
            BeginStreamingBinaryBulkLoad = buildAdjacencyIndex => new StreamingBulkLoader(
                _nodeStore, _relStore, _propStore,
                buildAdjacencyIndex ? _container : null),
        };
    }

    // *.quiver の親ディレクトリ (operational metadata = migrations.history の保存先)。
    public string DataDirectory => Path.GetDirectoryName(_containerPath) is { Length: > 0 } d ? d : ".";

    /// <summary>
    /// テスト専用 (torn-commit crash 再現): 全データページ + B+Tree 索引を fsync する
    /// (WAL truncate なし)。これにより未 checkpoint の committed データ (Suppressed FT leaf 含む) を
    /// disk へ落とし、Commit レコードだけ欠けた torn-commit の「body 保持」状態を決定論的に作れる。
    /// </summary>
    internal void FlushDataPagesForTest()
    {
        _pageManager.FlushAll();
        _indexManager.FlushAll();
    }

    // opt-in 列。catalog はテナント 16、各列テナントは 64+ (ColumnCatalog 採番)。
    // startup で eager に構築 (factory が注入)。write 経路 (GraphTransaction) と abort hook
    // (ReloadStoreMeta → ReloadColumns) の両方から参照される。
    private readonly ColumnManager _columnManager;

    /// <summary>write 経路 (列維持) のため GraphTransaction へ渡す列マネージャ。</summary>
    internal ColumnManager Columns => _columnManager;

    internal bool CreateColumn(EntityKind kind, int keyId)
    {
        // opt-in 列の DDL はアクティブ tx 無しを要求する。CreateColumn は
        // 現コミット済みデータから列を 1 パス構築するため、構築を跨ぐ並行 writer がいると列が
        // 取りこぼし、列スキャン集約が row path と乖離しうる。CompactAdjacency と同じ契約で塞ぐ。
        if (_txManager.ActiveCount > 0)
            throw new InvalidOperationException("CreateColumn requires no active transactions.");
        // 構築 (列データ / 列テナント page-table / catalog ページの書き込み) を
        // WAL 文脈下で行い commit する。これにより crash recovery / CreateSnapshot (online backup) が
        // 列ページを redo / 複製できる。tx 外で書くと clean Dispose のフラッシュ依存になり、
        // 未チェックポイント crash や snapshot で列が失われる。
        return RunColumnDdl(() => _columnManager.CreateColumn(kind, keyId));
    }

    internal bool DropColumn(EntityKind kind, int keyId)
    {
        if (_txManager.ActiveCount > 0)
            throw new InvalidOperationException("DropColumn requires no active transactions.");
        return RunColumnDdl(() => _columnManager.DropColumn(kind, keyId));
    }

    // 列 DDL の page 書き込みを WAL ログ + commit して durable 化する共通ラッパ。
    private bool RunColumnDdl(Func<bool> ddl)
    {
        var tx = _txManager.Begin(IsolationLevel.SnapshotIsolation);
        try
        {
            bool result = ddl();
            tx.Commit();
            return result;
        }
        catch
        {
            try { tx.Abort(); } catch { /* best-effort */ }
            throw;
        }
        finally { tx.Dispose(); }
    }
    internal bool TryGetColumn(EntityKind kind, int keyId, out ScalarColumnStore column)
        => _columnManager.TryGetColumn(kind, keyId, out column);

    /// <summary>
    /// 検証用 (interim): 列の可視値合計。列の登録/構築/永続を確認するために使用する。
    /// optimizer/operator 経由の本 read 経路に置き換わる予定。
    /// </summary>
    internal long ColumnProjectSumForTest(EntityKind kind, int keyId)
    {
        if (!TryGetColumn(kind, keyId, out var col)) return -1;
        var tx = _txManager.Begin(IsolationLevel.SnapshotIsolation);
        try
        {
            _ = tx.Relationships.Read(new RelationshipId(0)); // MvccContext を activate
            return col.ProjectSum(MvccContext.CurrentSnapshot, MvccContext.CurrentTxId, MvccContext.CurrentCommitted!);
        }
        finally { tx.Dispose(); }
    }

    public ITransactionManager Transactions => _txManager;
    public ISchemaApi Schema => _schema;
    public IDiagnosticsApi Diagnostics => _diagnostics;
    public IGraphAccessMethods Access => _access;
    public BulkLoadCapabilities BulkLoad => _bulkLoad;
    public IVectorStore Vectors => _vectorsFacade ??= new AutocommitVectorStore(
        _vectors, () => BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false));

    public IGraphTransaction BeginGraphTransaction(IsolationLevel level, bool readOnly)
    {
        var inner = _txManager.Begin(level);
        return new GraphTransaction(
            inner, _labelTokens, _relTypeTokens, _propKeyTokens,
            _hyperedgeTypeTokens, _roleTokens,
            readOnly,
            readOnly ? null : _logicalSink,
            _columnManager,
            _vectors);
    }

    /// <summary>
    /// イミュータブルな base 隣接ビューを現在のリレーションシップストアから再構築し、
    /// tombstone を除去して epoch を進める。呼び出し後、すべての生存エッジは base から供給され、
    /// 新しいリレーションシップが作成されるまで delta 走査は何も返さない。
    ///
    /// 現在は V1 ストア (payload lane なし) のみ対応。V2 ストアがアクティブな場合は
    /// 例外を投げる (V2 compact はプロパティストアから inline payload を再読する必要があり未実装)。
    /// 呼び出し元はアクティブなトランザクションが無いことを保証すること。
    /// </summary>
    public void CompactAdjacency()
    {
        if (_txManager.ActiveCount > 0)
            throw new InvalidOperationException(
                "CompactAdjacency requires no active transactions.");
        if (_adjStore is AdjacencyBlockStoreV2)
            throw new NotSupportedException(
                "CompactAdjacency for V2 (payload lane) is not yet implemented.");

        // 現在の adj ファイルを壊す前に生存 rels (id, src, tgt, type) をスナップショットする。
        // IRelationshipStore.Scan はストア順で id を返し、各読み出しがアクティブページから
        // src/tgt/type を取得する。
        var live = new List<(long Id, long Src, long Tgt, int TypeId)>();
        long maxId = -1;
        foreach (var relId in _relStore.Scan())
        {
            var r = _relStore.Read(relId);
            // 隣接ビルドへ渡す id は Sequence (packed Value ではない)。
            live.Add((relId.Sequence, r.Source.Sequence, r.Target.Sequence, r.Type.Value));
            if (relId.Sequence > maxId) maxId = relId.Sequence;
        }
        long newBaseHwm = maxId + 1; // リレーションシップが無ければ 0 — "no base" と一致

        // 隣接データは container 内テナントに同居する。Build は対象テナントを
        // truncate して作り直すため、旧 PagedFile を pageManager から drop する必要はない。
        if (_adjStore is AdjacencyBlockStore old) old.Dispose();
        _txManager.SwapAdjacencyStore(null);
        _adjStore = null;

        // 隣接インデックスをその場で再構築する。Build は論理ノード ID ごとに 1 エントリを持つ前提なので
        // nodeHwm を要求する。バルクロード後はこれ以外の情報が無いため、観測した src/tgt の最大値 + 1 を使う。
        long nodeHwm = 0;
        foreach (var (_, src, tgt, _) in live)
        {
            if (src + 1 > nodeHwm) nodeHwm = src + 1;
            if (tgt + 1 > nodeHwm) nodeHwm = tgt + 1;
        }

        var adjData = _container.OpenTenant(AdjacencyContainer.DataTenant, PageKind.AdjacencyBlock);
        var adjIdx = _container.OpenTenant(AdjacencyContainer.IndexTenant, PageKind.Header);
        AdjacencyBlockStore.Build(adjData, adjIdx, live, nodeHwm);

        // epoch メタデータをリセットして再オープン。ResetAfterCompact は epoch カウンタを
        // 進め (オブザーバが再構築を検出可能にする)、tombstone を破棄する
        // (新しい base ビューは生存エッジのみを含むため)。
        var epochTenant = _container.OpenTenant(AdjacencyContainer.EpochTenant, PageKind.Header);
        AdjacencyEpoch newEpoch = AdjacencyEpoch.Open(epochTenant);
        newEpoch.ResetAfterCompact(newBaseHwm);
        // CompactAdjacency は tx 外なので、再構築したページを durable 化する。
        _container.Flush();
        var newStore = new AdjacencyBlockStore(adjData, adjIdx, newEpoch);
        _adjStore = newStore;
        _txManager.SwapAdjacencyStore(newStore);
    }

    /// <summary>
    /// ライブスナップショット。
    ///
    /// 流れ:
    ///  1. ベストエフォートで <see cref="TransactionManager.RequestCheckpoint"/> を起動し、
    ///     ダーティページを fsync + WAL に CheckpointEnd を残す (アクティブ tx 0 の場合のみ成功)。
    ///     target 側 recovery の走査範囲を縮めるため。
    ///  2. <see cref="IPageManager.Files"/> を列挙して全ページファイルを page-by-page で複製。
    ///     <see cref="IPagedFile.PinForRead"/> でフレームレベル read lock を取りながら順次写すので、
    ///     並行 writer は同一ページが衝突するときだけ短い待ち時間を経験する (block しない)。
    ///  3. トークン / 隣接 / .fileKinds / .idxmeta などの非ページファイルを <see cref="File.Copy"/> で複製。
    ///     これらはいずれも <c>FileShare.Read</c> 以上で開かれているため外部から並行読みできる。
    ///  4. WAL を <see cref="IWriteAheadLog.FlushTo"/> で末尾までフラッシュしてから wal/*.log を複製。
    ///     データファイルを先に取って WAL を後に取る順序は重要: 並行 in-flight tx が <c>PinForWrite</c>
    ///     で吐く CompensationLogRecord (before-image) は <b>即時 WAL 追記</b>される (案 C の FlushPending
    ///     が後段でまとめる PageImage と異なる) ため、データコピー中にバッファ pool eviction で
    ///     uncommitted modification が target のデータファイルへ漏れたとしても、WAL コピーは必ず
    ///     その CLR を含み、target recovery の Pass 3 undo が正しく巻き戻せる。
    ///
    /// target の recovery 後 LSN は snapshot WAL 末尾 LSN まで進む。
    /// </summary>
    /// <summary>
    /// ノードストアの dead version 物理回収 + committed registry の prune。
    /// アクティブトランザクションが残っているときは安全側で何もせず Skip 報告する。
    /// </summary>
    public VacuumReport Vacuum(VacuumOptions? options = null)
    {
        // WAL を渡して、dead version 回収後の末尾連続 free page を物理 truncate する。
        // WAL の FileTruncate レコード経由で crash recovery に対する冪等再生を保証する。
        var vac = new Vacuum(
            _nodeStore, _relStore, _propStore,
            _txManager, _txManager.CommittedRegistry, _wal, _columnManager);
        return vac.Run(options);
    }

    public void CreateSnapshot(string targetFilePath, SnapshotOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetFilePath);
        options ??= new SnapshotOptions();

        // snapshot ターゲットも単一ファイル (*.quiver)。親ディレクトリを用意する。
        var parentDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

        _txManager.RequestCheckpoint();

        // 1. コンテナ (graph.quiver = コア / 索引 / 隣接 / token / epoch を同居) を page-by-page で
        //    複製する。PinForRead でフレームレベル read lock を取りながら写すので、並行 writer は
        //    同一ページ衝突時だけ短く待つ (block しない)。IncludeIndexes は単一ファイルでは no-op
        //    (索引はコンテナに同居するため常に含まれる)。
        CopyPagedFile(_container.Physical, targetFilePath);

        // 2. WAL を末尾までフラッシュしてから単一サイドカー *.quiver-wal を複製。
        //    Drain で出される PageImage 等もここで durable になる。target を開くと recovery が
        //    この WAL を replay して整合する。
        _wal.FlushTo(_wal.CurrentLsn);
        var srcWal = _containerPath + "-wal";
        if (File.Exists(srcWal))
            CopySharedFile(srcWal, targetFilePath + "-wal");
    }

    private static void CopyPagedFile(IPagedFile src, string dstPath)
    {
        long pageCount = src.PageCount;
        int pageSize = src.PageSize;
        using var fs = new FileStream(
            dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.SetLength(pageCount * pageSize);
        byte[] buf = new byte[pageSize];
        for (long p = 0; p < pageCount; p++)
        {
            var pageId = new Quiver.Core.PageId(p);
            using var handle = src.PinForRead(pageId);
            handle.Raw.CopyTo(buf);
            fs.Write(buf, 0, pageSize);
        }
        fs.Flush(flushToDisk: true);
    }

    private static void CopySharedFile(string srcPath, string dstPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        // FileShare.ReadWrite を立てておくと、source が WAL / トークン / メタファイルを
        // 並行で append しても EBUSY にならない。
        using var src = new FileStream(
            srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(
            dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
        dst.Flush(flushToDisk: true);
    }

    private bool _disposed;

    public void Dispose()
    {
        // 二重 Dispose ガード。クリーン終了処理は _physical 等へアクセスするため
        // 冪等でないので、2 回目以降は no-op にする (テストが db を二重 Dispose する経路がある)。
        if (_disposed) return;
        _disposed = true;

        // クリーン終了。アクティブ tx が無ければ全データを graph.quiver へ
        // durable 化し、WAL サイドカーを削除対象にする (静止時は graph.quiver のみ)。
        // ActiveCount==0 なので未コミットデータは存在せず、flush 後の graph.quiver は完全。
        bool cleanShutdown = _txManager.ActiveCount == 0;
        if (cleanShutdown)
        {
            // committed TxId 高水位を container へ永続化してから flush する。
            // WAL 削除後の reopen で MVCC visibility horizon と次 TxId 採番を復元するため。
            _container.SetCommittedHighWaterTxId(_txManager.PeekNextTxId());
            _pageManager.FlushAll();   // 全データページを fsync (container.Physical を含む)
            _indexManager.FlushAll();  // 索引も container 上だが念のため
            if (_wal is WriteAheadLog durableWal)
                durableWal.MarkDeleteOnDispose();
        }

        _txManager.Dispose();
        if (_adjStore is IDisposable d) d.Dispose();
        _indexManager.Dispose();
        _labelTokens.Dispose();
        _relTypeTokens.Dispose();
        _propKeyTokens.Dispose();
        _hyperedgeTypeTokens.Dispose();
        _roleTokens.Dispose();
        // WAL を dispose する前にデータファイルを flush する。PagedFile.Flush() は
        // write-ahead 順序 (WAL→データ) に従うため、page manager がダーティフレームを
        // ディスクへ flush する間は WAL がまだ生きている必要がある。
        _pageManager.Dispose();
        _wal.Dispose();
    }
}

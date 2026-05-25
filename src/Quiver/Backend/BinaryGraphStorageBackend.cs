using Quiver.Core;
using Quiver.Index;
using Quiver.Logical;
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
    private readonly ILogicalMutationSink? _logicalSink;

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
        IVectorStore vectors,
        LabelNodeIndex? labelIndex = null,
        ILogicalMutationSink? logicalSink = null,
        TimeSpan? adaptiveTargetRecoveryTime = null,
        long adaptiveMinThresholdBytes = 4L * 1024 * 1024,
        long adaptiveMaxThresholdBytes = 1024L * 1024 * 1024,
        int adaptiveSampleWindow = 1000)
    {
        _logicalSink = logicalSink;
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
        // FT-22: index manager と label index を DiagnosticsApi に渡して
        // CheckIndexConsistency / RepairIndexes が機能するようにする。
        // FT-28: TransactionManager を渡し、CurrentCheckpointThresholdBytes /
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
                buildAdjacencyIndex ? _directoryPath : null),
            BeginStreamingBinaryBulkLoad = buildAdjacencyIndex => new StreamingBulkLoader(
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
        return new GraphTransaction(
            inner, _labelTokens, _relTypeTokens, _propKeyTokens,
            readOnly,
            // BA-7: skip the recorder entirely for read-only transactions and
            // when no sink is configured so the hot path stays allocation-free.
            readOnly ? null : _logicalSink);
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

        // 隣接ファイルをその場で再構築する。Build は論理ノード ID ごとに 1 エントリを持つ前提なので
        // nodeHwm を要求する。バルクロード後はこれ以外の情報が無いため、観測した src/tgt の最大値 + 1 を使う。
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

    /// <summary>
    /// OP-1: ライブスナップショット。
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
    public void CreateSnapshot(string targetDirectory, SnapshotOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        options ??= new SnapshotOptions();

        Directory.CreateDirectory(targetDirectory);

        _txManager.RequestCheckpoint();

        // 1. ページファイル群を page-by-page で複製。データファイル + 隣接 PagedFile が対象。
        //    索引 PagedFile は IndexManager が直接 new するため IPageManager.Files には居ない。
        //    index PagedFile は下の IndexFiles ループでカバーする。
        foreach (var src in _pageManager.Files)
        {
            string srcPath = src.Path;
            if (string.IsNullOrEmpty(srcPath)) continue;
            string relative = Path.GetRelativePath(_directoryPath, srcPath);
            if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
            if (relative.Length == 0 || relative == ".") continue;

            string dstPath = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
            CopyPagedFile(src, dstPath);
        }

        // 1b. 索引 PagedFile (.idx)。IncludeIndexes=false なら丸ごとスキップ。
        if (options.IncludeIndexes)
        {
            foreach (var src in _indexManager.IndexFiles)
            {
                string srcPath = src.Path;
                if (string.IsNullOrEmpty(srcPath)) continue;
                string relative = Path.GetRelativePath(_directoryPath, srcPath);
                if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
                string dstPath = Path.Combine(targetDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                CopyPagedFile(src, dstPath);
            }
        }

        // 2. 非ページファイル (トークン / 隣接 / メタ) を File.Copy で複製。
        CopyAuxiliaryFiles(targetDirectory, options);

        // 3. WAL を末尾までフラッシュしてからセグメントファイルを複製。
        //    Drain で出される PageImage 等もここで durable になる。
        _wal.FlushTo(_wal.CurrentLsn);
        var srcWalDir = Path.Combine(_directoryPath, "wal");
        var dstWalDir = Path.Combine(targetDirectory, "wal");
        Directory.CreateDirectory(dstWalDir);
        if (Directory.Exists(srcWalDir))
        {
            foreach (var seg in Directory.GetFiles(srcWalDir, "wal.*.log"))
            {
                var dst = Path.Combine(dstWalDir, Path.GetFileName(seg));
                CopySharedFile(seg, dst);
            }
        }
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

    private void CopyAuxiliaryFiles(string targetDirectory, SnapshotOptions options)
    {
        // トークンストア (FileShare.Read で開かれている)
        foreach (var name in new[] { "labels.tok", "reltypes.tok", "propkeys.tok" })
            CopySharedFileIfExists(name, targetDirectory);

        // 隣接ブロックの sidecar (PagedFile 経由は .db のみ。idx / meta / epoch は別ファイル)
        foreach (var name in new[] {
            "adj_idx.dat", "adj_v2_idx.dat", "adj_v2.meta", "adj.epoch" })
        {
            CopySharedFileIfExists(name, targetDirectory);
        }

        if (!options.IncludeIndexes) return;

        // 索引 sidecar: .idxmeta + .fileKinds
        var srcIdxDir = Path.Combine(_directoryPath, "indexes");
        if (!Directory.Exists(srcIdxDir)) return;
        var dstIdxDir = Path.Combine(targetDirectory, "indexes");
        Directory.CreateDirectory(dstIdxDir);

        foreach (var metaPath in Directory.GetFiles(srcIdxDir, "*.idxmeta"))
            CopySharedFile(metaPath, Path.Combine(dstIdxDir, Path.GetFileName(metaPath)));

        var kindsPath = Path.Combine(srcIdxDir, ".fileKinds");
        if (File.Exists(kindsPath))
            CopySharedFile(kindsPath, Path.Combine(dstIdxDir, ".fileKinds"));
    }

    private void CopySharedFileIfExists(string fileName, string targetDirectory)
    {
        var src = Path.Combine(_directoryPath, fileName);
        if (!File.Exists(src)) return;
        var dst = Path.Combine(targetDirectory, fileName);
        CopySharedFile(src, dst);
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

    public void Dispose()
    {
        _txManager.Dispose();
        if (_adjStore is IDisposable d) d.Dispose();
        _indexManager.Dispose();
        _labelTokens.Dispose();
        _relTypeTokens.Dispose();
        _propKeyTokens.Dispose();
        // FT-15: flush the data files BEFORE disposing the WAL. PagedFile.Flush()
        // now does WAL-before-data (write-ahead) ordering, so the WAL must still
        // be alive while the page manager flushes its dirty frames to disk.
        _pageManager.Dispose();
        _wal.Dispose();
    }
}

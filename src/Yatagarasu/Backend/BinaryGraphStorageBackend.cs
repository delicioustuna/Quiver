using Yatagarasu.Core;
using Yatagarasu.Index;
using Yatagarasu.Index.Vector;
using Yatagarasu.Index.FullText;
using Yatagarasu.Logical;
using Yatagarasu.Maintenance;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;
using Yatagarasu.Storage.Wal;
using Yatagarasu.Telemetry;
using System.Diagnostics;

namespace Yatagarasu;

internal sealed class BinaryGraphStorageBackend : IGraphStorageBackendInternal
{
    internal static Action<CompactAdjacencyPhase>? CompactAdjacencyPhaseInjector;
    internal static Action? ScalarIndexArtifactBuiltForTest;
    internal static Action<ScalarIndexRebuildPhase>? ScalarIndexRebuildPhaseInjector;
    internal static Action? VectorSegmentBuildStartedForTest;
    internal static Action? VectorSegmentBuildCompletedForTest;
    internal static Action<TimeSpan>? VectorSegmentPublishMeasuredForTest;
    internal static Action? FullTextSegmentBuildStartedForTest;
    internal static Action? FullTextSegmentBuildCompletedForTest;
    internal static Action<TimeSpan>? FullTextSegmentPublishMeasuredForTest;

    private readonly IVectorDefinitionCatalog _vectorDefinitions;
    private readonly VectorSegmentIndex _vectorSegments = new();
    private readonly FullTextSegmentIndex _fullTextSegments;
    private readonly PageManager _pageManager;
    private readonly IWriteAheadLog _wal;
    private readonly VersionedVertexStore _vertexStore;
    private readonly VersionedEdgeStore _edgeStore;
    private readonly PropertyVersionStore _propStore;
    private readonly LabelTokenStore _labelTokens;
    private readonly EdgeTypeTokenStore _edgeTypeTokens;
    private readonly PropertyKeyTokenStore _propKeyTokens;
    private readonly NexusTypeTokenStore _nexusTypeTokens;
    private readonly RoleTokenStore _roleTokens;
    private readonly IndexManager _indexManager;
    private readonly NexusMergeIndex _nexusMergeIndex = new();
    // immutable adjacency segment view を保持する。
    // CompactAdjacency が再構築したストアを差し替えるため mutable。
    // 隣接データは container 内テナントに同居するため、別 PagedFile の所有は不要。
    private IAdjacencySegmentStore? _adjStore;
    private readonly ICoMembershipBlockStore? _coMembershipStore;
    // bulk load / CompactAdjacency が隣接テナントを構築するために保持する。
    private readonly SingleFileContainer _container;
    private readonly TransactionManager _txManager;
    private readonly SchemaApi _schema;
    private readonly DiagnosticsApi _diagnostics;
    private readonly IGraphAccessMethods _access;
    private readonly BulkLoadCapabilities _bulkLoad;
    // 単一コンテナ (*.yata) のフルパス。WAL サイドカー = _containerPath + "-wal"。
    private readonly string _containerPath;
    private readonly ILogicalMutationSink? _logicalSink;
    private readonly EdgeDeltaHeadStore? _edgeDeltaHeads;
    private readonly PersistentEdgeDeltaStore? _edgeDeltas;
    private readonly DatabaseIdentityStore _databaseIdentity;
    private readonly Lock _databaseIdentityGate = new();
    private readonly RelationshipReuseCoordinator _relationshipReuse;
    private readonly CancellationTokenSource _scalarIndexRebuildCancellation = new();
    private readonly object _scalarIndexRebuildSync = new();
    private Task? _scalarIndexRebuildTask;
    private int _scalarIndexRebuildRequested;
    private Exception? _scalarIndexRebuildError;
    private readonly CancellationTokenSource _vectorSegmentMergeCancellation = new();
    private readonly object _vectorSegmentMergeSync = new();
    private Task? _vectorSegmentMergeTask;
    private int _vectorSegmentMergeRequested;
    private Exception? _vectorSegmentMergeError;
    private readonly CancellationTokenSource _fullTextSegmentMergeCancellation = new();
    private readonly object _fullTextSegmentMergeSync = new();
    private Task? _fullTextSegmentMergeTask;
    private int _fullTextSegmentMergeRequested;
    private Exception? _fullTextSegmentMergeError;

    internal BinaryGraphStorageBackend(
        string containerPath,
        SingleFileContainer container,
        PageManager pageManager,
        IWriteAheadLog wal,
        VersionedVertexStore vertexStore,
        VersionedEdgeStore edgeStore,
        PropertyVersionStore propStore,
        LabelTokenStore labelTokens,
        EdgeTypeTokenStore edgeTypeTokens,
        PropertyKeyTokenStore propKeyTokens,
        NexusTypeTokenStore nexusTypeTokens,
        RoleTokenStore roleTokens,
        IndexManager indexManager,
        IAdjacencySegmentStore? adjStore,
        TransactionManager txManager,
        BinaryGraphAccessMethods access,
        IVectorDefinitionCatalog vectorDefinitions,
        ICoMembershipBlockStore? coMembershipStore = null,
        LabelVertexIndex? labelIndex = null,
        EdgeDeltaHeadStore? edgeDeltaHeads = null,
        PersistentEdgeDeltaStore? edgeDeltas = null,
        DatabaseIdentityStore? databaseIdentity = null,
        ILogicalMutationSink? logicalSink = null,
        TimeSpan? adaptiveTargetRecoveryTime = null,
        long adaptiveMinThresholdBytes = 4L * 1024 * 1024,
        long adaptiveMaxThresholdBytes = 1024L * 1024 * 1024,
        int adaptiveSampleWindow = 1000)
    {
        _logicalSink = logicalSink;
        _containerPath = containerPath;
        _container = container;
        _vectorDefinitions = vectorDefinitions;
        _pageManager = pageManager;
        _wal = wal;
        _vertexStore = vertexStore;
        _edgeStore = edgeStore;
        _propStore = propStore;
        _labelTokens = labelTokens;
        _edgeTypeTokens = edgeTypeTokens;
        _propKeyTokens = propKeyTokens;
        _nexusTypeTokens = nexusTypeTokens;
        _roleTokens = roleTokens;
        _indexManager = indexManager;
        _fullTextSegments = new FullTextSegmentIndex(
            string.IsNullOrEmpty(_containerPath)
                ? null
                : _containerPath + "-ftseg",
            _indexManager.ResolveTokenizer,
            _indexManager.MarkFullTextRebuildRequiredInMemory,
            _labelTokens,
            _edgeTypeTokens,
            _nexusTypeTokens,
            _propKeyTokens);
        _adjStore = adjStore;
        _coMembershipStore = coMembershipStore;
        _edgeDeltaHeads = edgeDeltaHeads;
        _edgeDeltas = edgeDeltas;
        _databaseIdentity = databaseIdentity
            ?? new DatabaseIdentityStore(container, createIfMissing: false);
        _txManager = txManager;
        _relationshipReuse = new RelationshipReuseCoordinator(
            _container.OpenTenant(
                BinaryGraphStorageBackendFactory.TenantRelationshipReuse,
                PageKind.Header),
            _edgeStore,
            CompactAdjacencyCore,
            _container.Flush);

        _schema = new SchemaApi(_labelTokens, _edgeTypeTokens, _propKeyTokens, _indexManager,
            nexusTypes: _nexusTypeTokens,
            roles: _roleTokens,
            vectorDefinitions: _vectorDefinitions,
            acquireMutationLease: _txManager.AcquireMutationLease,
            acquireOwnedMutationLease: owner => _txManager.AcquireMutationLease(owner));
        _vectorSegments.MergeRequested = QueueVectorSegmentMerge;
        _fullTextSegments.MergeRequested = QueueFullTextSegmentMerge;
        _fullTextSegments.CatalogStateChanged = _schema.UpdateCommittedIndexState;
        _txManager.FullTextSegments = _fullTextSegments;
        // index manager と label index を DiagnosticsApi に渡して
        // CheckIndexConsistency / RepairIndexes が機能するようにする。
        // TransactionManager を渡し、CurrentCheckpointThresholdBytes /
        // SetCheckpointPolicy をホットスワップ経路として公開する。Adaptive 用パラメタは
        // factory で既知の options 値を持つので、後段で AttachAdaptiveDefaults により上書き可能。
        _diagnostics = new DiagnosticsApi(
            _vertexStore, _edgeStore, access, _propStore,
            _txManager.NexusStore, _txManager.IncidenceStore, _txManager.VertexIncidenceHeadStore,
            _indexManager, labelIndex, _txManager,
            adaptiveTargetRecoveryTime,
            adaptiveMinThresholdBytes,
            adaptiveMaxThresholdBytes,
            adaptiveSampleWindow);
        _access = access;
        _bulkLoad = new BulkLoadCapabilities
        {
            BeginBinaryBulkLoad = buildAdjacencyIndex => new BulkLoader(
                _vertexStore, _edgeStore, _propStore,
                buildAdjacencyIndex ? _container : null,
                _txManager.AcquireMutationLease(),
                ValidateBulkUniqueConstraints,
                RefreshDerivedIndexesAfterBulkLoad),
            BeginStreamingBinaryBulkLoad = buildAdjacencyIndex => new StreamingBulkLoader(
                _vertexStore, _edgeStore, _propStore,
                buildAdjacencyIndex ? _container : null,
                _txManager.AcquireMutationLease(),
                ValidateBulkUniqueConstraints,
                RefreshDerivedIndexesAfterBulkLoad),
        };

        using (_txManager.AcquireMutationLease())
            _relationshipReuse.Resume();

        if (_indexManager.ListIndexDefinitions()
            .Any(x => x.State != IndexLifecycleState.Ready))
        {
            Volatile.Write(ref _scalarIndexRebuildRequested, 1);
            QueueScalarIndexRebuild();
        }
    }

    private void RefreshDerivedIndexesAfterBulkLoad()
    {
        using ITransaction transaction = _txManager.BeginWrite();
        ScalarIndexMetadata[] definitions =
            [.. transaction.Indexes.ListIndexDefinitions()];
        FullTextCatalogEntry[] fullTextDefinitions =
            [.. transaction.Indexes.ListFullTextCatalogEntries()];
        foreach (ScalarIndexMetadata metadata in definitions)
            transaction.Indexes.SetIndexState(
                metadata.Definition.Name,
                IndexLifecycleState.RebuildRequired);
        foreach (FullTextCatalogEntry definition in fullTextDefinitions)
            transaction.Indexes.UpdateFullTextManifest(
                definition.Name,
                definition.Manifest,
                IndexLifecycleState.RebuildRequired);
        transaction.Commit();
        _fullTextSegments.InvalidateAll();
        foreach (FullTextCatalogEntry definition in fullTextDefinitions)
            _schema.UpdateCommittedIndexState(
                definition.Name,
                IndexLifecycleState.RebuildRequired);
        if (definitions.Length > 0)
        {
            Volatile.Write(ref _scalarIndexRebuildRequested, 1);
            QueueScalarIndexRebuild();
        }
    }

    private void ValidateBulkUniqueConstraints(BulkLoadConstraintInput input)
    {
        ScalarIndexDefinition[] definitions = _indexManager.ListIndexDefinitions()
            .Select(metadata => metadata.Definition)
            .Where(definition => definition.Unique)
            .ToArray();
        if (definitions.Length == 0)
            return;

        var labelByVertex = input.Vertices.ToDictionary(vertex => vertex.Id, vertex => vertex.LabelId);
        foreach (ScalarIndexDefinition definition in definitions)
        {
            if (!_labelTokens.TryGet(definition.Target.Scope!, out LabelId labelId)
                || !_propKeyTokens.TryGet(definition.Target.PropertyKey, out PropertyKeyId keyId))
                continue;

            var ownerByValue = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach ((long vertexId, List<BulkPropertyEntry> properties) in input.PropertiesByVertex)
            {
                if (!labelByVertex.TryGetValue(vertexId, out int ownerLabel)
                    || ownerLabel != labelId.Value)
                    continue;

                foreach (BulkPropertyEntry property in properties)
                {
                    if (property.KeyId != keyId.Value)
                        continue;
                    if (property.Type != PropertyValueType.String)
                    {
                        throw new ConstraintException(
                            $"Unique index '{definition.Name}' requires string values for " +
                            $"property '{definition.Target.PropertyKey}'.");
                    }

                    string text = System.Text.Encoding.UTF8.GetString(property.Data ?? []);
                    if (ownerByValue.TryGetValue(text, out long existingOwner)
                        && existingOwner != vertexId)
                    {
                        throw new UniqueConstraintViolationException(
                            definition.Name,
                            definition.Target.Scope!,
                            definition.Target.PropertyKey);
                    }
                    ownerByValue[text] = vertexId;
                }
            }
        }
    }

    private void QueueScalarIndexRebuild()
    {
        if (_disposed || Volatile.Read(ref _scalarIndexRebuildRequested) == 0)
            return;

        lock (_scalarIndexRebuildSync)
        {
            if (_scalarIndexRebuildTask is { IsCompleted: false })
                return;
            _scalarIndexRebuildTask = Task.Run(
                () => RebuildScalarIndexes(_scalarIndexRebuildCancellation.Token));
        }
    }

    private void RebuildScalarIndexes(CancellationToken cancellationToken)
    {
        using IDisposable rebuildMeasurement = YatagarasuTelemetry.TrackRebuild();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ScalarIndexBuildArtifact[] artifacts;
                using (ITransaction read = _txManager.BeginRead())
                {
                    ScalarIndexMetadata[] pending = read.Indexes
                        .ListIndexDefinitions()
                        .Where(x => x.State != IndexLifecycleState.Ready)
                        .ToArray();
                    if (pending.Length == 0)
                    {
                        Volatile.Write(ref _scalarIndexRebuildRequested, 0);
                        return;
                    }

                    // primary scan、key decode、sort は snapshot reader だけで行う。
                    // writer lease は下の source generation 検証と publish にだけ使う。
                    artifacts = pending
                        .Select(x => _schema.BuildScalarIndexArtifact(
                            read,
                            x.Definition))
                        .ToArray();
                }

                ScalarIndexRebuildPhaseInjector?.Invoke(
                    ScalarIndexRebuildPhase.AfterArtifactBuilt);
                Interlocked.Exchange(ref ScalarIndexArtifactBuiltForTest, null)?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                using ITransaction publish = _txManager.BeginWrite();
                _ = _schema.Bind(publish, readOnly: false);
                long oldestReader = _txManager.Snapshots.OldestCommittedHighWater(
                    publish.Snapshot.CommittedHighWater);
                bool accepted = artifacts.All(
                    artifact => oldestReader >= artifact.SourceCommittedHighWater);
                foreach (ScalarIndexBuildArtifact artifact in artifacts)
                {
                    if (!accepted
                        || !_schema.TryPublishScalarIndexArtifact(publish, artifact))
                    {
                        accepted = false;
                        break;
                    }
                }

                if (accepted)
                {
                    ScalarIndexRebuildPhaseInjector?.Invoke(
                        ScalarIndexRebuildPhase.BeforePublishCommit);
                    publish.Commit();
                    ScalarIndexRebuildPhaseInjector?.Invoke(
                        ScalarIndexRebuildPhase.AfterPublishCommit);
                    continue;
                }

                publish.Abort();
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(10)))
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _scalarIndexRebuildError = ex;
            // rebuild failure is a derived-data failure。
            // definition remains non-Ready, so query continues to use the same-snapshot fallback。
            Trace.TraceWarning(
                $"[Yatagarasu] Scalar index rebuild was deferred after an error: {ex}");
        }
    }

    // *.yata の親ディレクトリ。backend-local artifact の配置基準として保持する。
    public string DataDirectory => Path.GetDirectoryName(_containerPath) is { Length: > 0 } d ? d : ".";

    /// <summary>
    /// テスト専用 (torn-commit crash 再現): 全データページ + B+Tree 索引を fsync する
    /// (WAL truncate なし)。これにより未 checkpoint の committed データを
    /// disk へ落とし、Commit レコードだけ欠けた torn-commit の「body 保持」状態を決定論的に作れる。
    /// </summary>
    internal void FlushDataPagesForTest()
    {
        _pageManager.FlushAll();
        _indexManager.FlushAll();
    }

    internal void RequestCheckpointForTest()
    {
        try
        {
            _txManager.RequestCheckpoint();
        }
        catch
        {
            // phase injector が checkpoint を中断した後の Dispose を clean shutdown にしない。
            _txManager.MarkFaulted();
            throw;
        }
    }

    public ITransactionManager Transactions => _txManager;
    internal ISchemaCatalog Schema => _schema.CommittedCatalog;
    internal SchemaApi SchemaApiForTesting => _schema;
    public ISchemaCatalog SchemaCatalog => _schema.CommittedCatalog;
    public IDiagnosticsApi Diagnostics => _diagnostics;

    // 整合性テストは公開 API では作れない破損を明示的に注入する必要がある。
    // ストア実体だけを internal に露出し、通常の利用者が物理レコードを変更する経路にはしない。
    internal INexusStore NexusStoreForTest => _txManager.NexusStore;
    internal IIncidenceStore IncidenceStoreForTest => _txManager.IncidenceStore;
    internal long CoMembershipReadCountForTest
        => (_coMembershipStore as CoMembershipBlockStore)?.ReadCount ?? 0;
    internal Exception? ScalarIndexRebuildErrorForTest => _scalarIndexRebuildError;
    internal Exception? VectorSegmentMergeErrorForTest => _vectorSegmentMergeError;
    internal void WaitForVectorSegmentMergeForTest()
    {
        while (true)
        {
            Task? task;
            lock (_vectorSegmentMergeSync)
                task = _vectorSegmentMergeTask;
            task?.GetAwaiter().GetResult();
            lock (_vectorSegmentMergeSync)
            {
                if (_vectorSegmentMergeTask == task
                    && Volatile.Read(ref _vectorSegmentMergeRequested) == 0)
                    return;
            }
        }
    }
    internal Exception? FullTextSegmentMergeErrorForTest => _fullTextSegmentMergeError;
    internal long FullTextPrimaryFallbackScanCountForTest
        => _fullTextSegments.PrimaryFallbackScanCountForTest;
    internal void WaitForFullTextSegmentMergeForTest()
    {
        while (true)
        {
            Task? task;
            lock (_fullTextSegmentMergeSync)
                task = _fullTextSegmentMergeTask;
            task?.GetAwaiter().GetResult();
            lock (_fullTextSegmentMergeSync)
            {
                if (_fullTextSegmentMergeTask == task
                    && Volatile.Read(ref _fullTextSegmentMergeRequested) == 0)
                    return;
            }
        }
    }
    internal IVertexIncidenceHeadStore VertexIncidenceHeadStoreForTest => _txManager.VertexIncidenceHeadStore;
    public IGraphAccessMethods Access => _access;
    public BulkLoadCapabilities BulkLoad => _bulkLoad;
    public bool TryGetDatabaseInstanceId(out DatabaseInstanceId databaseInstanceId)
    {
        lock (_databaseIdentityGate)
            return _databaseIdentity.TryGet(out databaseInstanceId);
    }
    public DatabaseInstanceId EnsureDatabaseInstanceId()
    {
        lock (_databaseIdentityGate)
        {
            if (_databaseIdentity.TryGet(out DatabaseInstanceId databaseInstanceId))
                return databaseInstanceId;

            // Legacy DB でのtenant追加はcatalog、page table、headerの複数pageを
            // 書き換える。独立write transactionのWAL commit完了後だけIDを
            // readerへ公開し、crash途中の部分的なtenantを正本にしない。
            using ITransaction identityTransaction = _txManager.BeginWrite();
            DatabaseInstanceId created = _databaseIdentity.PrepareCreate();
            identityTransaction.OnCommitted(
                () => _databaseIdentity.PublishCreated(created));
            identityTransaction.Commit();
            return created;
        }
    }
    public IReadTransaction BeginReadTransaction()
        => new ReadTransaction(WrapGraphTransaction(_txManager.BeginRead(), readOnly: true));

    public IWriteTransaction BeginWriteTransaction()
    {
        EnsureDatabaseInstanceId();
        if (Volatile.Read(ref _scalarIndexRebuildRequested) == 0
            && _indexManager.ListIndexDefinitions()
                .Any(x => x.State != IndexLifecycleState.Ready))
        {
            Volatile.Write(ref _scalarIndexRebuildRequested, 1);
        }
        QueueScalarIndexRebuild();
        return new WriteTransaction(WrapGraphTransaction(
            _txManager.BeginWrite(),
            readOnly: false));
    }

    private GraphTransaction WrapGraphTransaction(ITransaction inner, bool readOnly)
    {
        return new GraphTransaction(
            inner, _labelTokens, _edgeTypeTokens, _propKeyTokens,
            _nexusTypeTokens, _roleTokens,
            _schema.Bind(inner, readOnly),
            readOnly,
            readOnly ? null : _logicalSink,
            _vectorSegments,
            _fullTextSegments,
            _nexusMergeIndex);
    }

    private void QueueVectorSegmentMerge()
    {
        if (_disposed)
            return;
        Volatile.Write(ref _vectorSegmentMergeRequested, 1);
        lock (_vectorSegmentMergeSync)
        {
            if (_vectorSegmentMergeTask is { IsCompleted: false })
                return;
            _vectorSegmentMergeTask = Task.Run(() =>
            {
                try
                {
                    MergeVectorSegments(_vectorSegmentMergeCancellation.Token);
                }
                finally
                {
                    lock (_vectorSegmentMergeSync)
                        _vectorSegmentMergeTask = null;
                    if (!_disposed
                        && Volatile.Read(ref _vectorSegmentMergeRequested) != 0)
                        QueueVectorSegmentMerge();
                }
            });
        }
    }

    private void MergeVectorSegments(CancellationToken cancellationToken)
    {
        using IDisposable rebuildMeasurement = YatagarasuTelemetry.TrackRebuild();
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && Interlocked.Exchange(ref _vectorSegmentMergeRequested, 0) != 0)
            {
                IReadOnlyList<VectorSegmentBuildSource> sources =
                    _vectorSegments.CaptureBuildSources();
                foreach (VectorSegmentBuildSource source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<VectorSegmentMutation> entries;
                    using (ITransaction read = _txManager.BeginRead())
                    {
                        entries = VectorSegmentIndex.ScanPrimary(
                            read,
                            source.Definition,
                            _labelTokens,
                            _edgeTypeTokens,
                            _nexusTypeTokens,
                            _propKeyTokens);
                    }

                    // artifact構築はread snapshotの内容だけを使い、writer leaseを保持しない。
                    VectorSegmentBuildStartedForTest?.Invoke();
                    VectorSegmentBuildArtifact artifact =
                        VectorSegmentIndex.Build(source, entries);
                    VectorSegmentBuildCompletedForTest?.Invoke();
                    using ITransaction publish = _txManager.BeginWrite();
                    var publishDuration = Stopwatch.StartNew();
                    long publishTransactionId = publish.Id.Value;
                    publish.OnCommitted(() =>
                    {
                        IndexDefinition? currentDefinition = null;
                        if (_schema.CommittedCatalog.TryGetIndex(
                                source.IndexName,
                                out IndexInfo current))
                            currentDefinition = current.Definition;
                        if (!_vectorSegments.TryPublishMerge(
                                publishTransactionId,
                                artifact,
                                currentDefinition))
                        {
                            artifact.Segment.Dispose();
                            Volatile.Write(ref _vectorSegmentMergeRequested, 1);
                        }
                    });
                    publish.Commit();
                    publishDuration.Stop();
                    VectorSegmentPublishMeasuredForTest?.Invoke(
                        publishDuration.Elapsed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _vectorSegmentMergeError = ex;
        }
    }

    private void QueueFullTextSegmentMerge()
    {
        if (_disposed)
            return;
        Volatile.Write(ref _fullTextSegmentMergeRequested, 1);
        lock (_fullTextSegmentMergeSync)
        {
            if (_fullTextSegmentMergeTask is { IsCompleted: false })
                return;
            _fullTextSegmentMergeTask = Task.Run(() =>
            {
                try
                {
                    MergeFullTextSegments(_fullTextSegmentMergeCancellation.Token);
                }
                finally
                {
                    lock (_fullTextSegmentMergeSync)
                        _fullTextSegmentMergeTask = null;
                    if (!_disposed
                        && Volatile.Read(ref _fullTextSegmentMergeRequested) != 0)
                        QueueFullTextSegmentMerge();
                }
            });
        }
    }

    private void MergeFullTextSegments(CancellationToken cancellationToken)
    {
        using IDisposable rebuildMeasurement = YatagarasuTelemetry.TrackRebuild();
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && Interlocked.Exchange(ref _fullTextSegmentMergeRequested, 0) != 0)
            {
                IReadOnlyList<FullTextSegmentBuildSource> sources =
                    _fullTextSegments.CaptureBuildSources();
                foreach (FullTextSegmentBuildSource source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<FullTextSegmentMutation> entries;
                    long sourceCommittedHighWater;
                    using (ITransaction read = _txManager.BeginRead())
                    {
                        entries = FullTextSegmentIndex.ScanPrimary(
                            read,
                            source.Definition,
                            _labelTokens,
                            _edgeTypeTokens,
                            _nexusTypeTokens,
                            _propKeyTokens);
                        sourceCommittedHighWater =
                            read.Snapshot.CommittedHighWater;
                    }

                    FullTextSegmentBuildStartedForTest?.Invoke();
                    FullTextSegmentBuildArtifact artifact =
                        _fullTextSegments.Build(
                            source,
                            entries,
                            sourceCommittedHighWater);
                    FullTextSegmentBuildCompletedForTest?.Invoke();
                    using ITransaction publish = _txManager.BeginWrite();
                    var publishDuration = Stopwatch.StartNew();
                    if (!_fullTextSegments.TryPrepareMerge(publish, artifact))
                    {
                        artifact.Segment.Dispose();
                        Volatile.Write(ref _fullTextSegmentMergeRequested, 1);
                        publish.Abort();
                        continue;
                    }
                    publish.Commit();
                    publishDuration.Stop();
                    FullTextSegmentPublishMeasuredForTest?.Invoke(
                        publishDuration.Elapsed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _fullTextSegmentMergeError = ex;
        }
    }

    /// <summary>
    /// イミュータブルな base 隣接ビューを現在のEdgeストアから再構築し、
    /// tombstone を除去して epoch を進める。呼び出し後、すべての生存エッジは base から供給され、
    /// 新しいEdgeが作成されるまで delta 走査は何も返さない。
    ///
    /// writer lease が更新を直列化し、既存 reader は snapshot visibility で結果を絞り込む。
    /// </summary>
    public void CompactAdjacency()
    {
        using var mutationLease = _txManager.AcquireMutationLease();
        CompactAdjacencyCore();
    }

    private void CompactAdjacencyCore()
    {
        PayloadLaneSpec payloadSpec = (_adjStore as IAdjacencyPayloadView)?.PayloadSpec
            ?? new PayloadLaneSpec(PayloadKind.None, -1, 0);

        // 現在の adj ファイルを壊す前に生存 edges (id, src, tgt, type) をスナップショットする。
        // IEdgeStore.Scan はストア順で id を返し、各読み出しがアクティブページから
        // src/tgt/type を取得する。
        var live = new List<(long Id, long Src, long Tgt, int TypeId)>();
        long maxId = -1;
        foreach (var edgeId in _edgeStore.Scan())
        {
            var r = _edgeStore.Read(edgeId);
            // 隣接ビルドへ渡す id は Sequence (packed Value ではない)。
            live.Add((edgeId.Sequence, r.Source.Sequence, r.Target.Sequence, r.Type.Value));
            if (edgeId.Sequence > maxId) maxId = edgeId.Sequence;
        }
        long newBaseHwm = maxId + 1; // Edgeが無ければ 0 — "no base" と一致
        Dictionary<long, long>? weights = null;
        if (payloadSpec.Kind != PayloadKind.None)
        {
            weights = CaptureExistingPayloads(live);
            foreach (var (id, _, _, _) in live)
            {
                var edgeId = EdgeId.Create(id, _edgeStore.CurrentGeneration(id));
                if (TryReadPayload(edgeId, payloadSpec, out long raw))
                    weights[edgeId.Sequence] = raw;
            }
        }

        long vertexHwm = 0;
        foreach (var (_, src, tgt, _) in live)
        {
            if (src + 1 > vertexHwm) vertexHwm = src + 1;
            if (tgt + 1 > vertexHwm) vertexHwm = tgt + 1;
        }

        var adjData = _container.OpenTenant(AdjacencyContainer.DataTenant, PageKind.AdjacencyBlock);
        var adjIdx = _container.OpenTenant(AdjacencyContainer.IndexTenant, PageKind.Header);

        // compact は導出ビューの再構築であり、正本は edge store にある。
        // 先に descriptor を無効化して durable 化しておくと、以降の crash/reopen は
        // 部分的な adjacency view を開かず、row path へ安全にフォールバックできる。
        if (_adjStore is IDisposable old) old.Dispose();
        _txManager.SwapAdjacencyStore(null, writerLeaseHeld: true);
        _adjStore = null;
        AdjacencyContainer.WriteDescriptor(adjData, AdjacencyContainer.KindNone, null);
        _container.Flush();
        CompactAdjacencyPhaseInjector?.Invoke(CompactAdjacencyPhase.AfterDescriptorInvalidated);

        // 隣接インデックスをその場で再構築する。Build は論理Vertex ID ごとに 1 エントリを持つ前提なので
        // vertexHwm を要求する。バルクロード後はこれ以外の情報が無いため、観測した src/tgt の最大値 + 1 を使う。
        AdjacencySegmentStore.Build(
            adjData, adjIdx, live, weights ?? [], vertexHwm, payloadSpec, writeDescriptor: false);
        CompactAdjacencyPhaseInjector?.Invoke(CompactAdjacencyPhase.AfterRebuild);

        // epoch メタデータをリセットして再オープン。ResetAfterCompact は epoch カウンタを
        // 進め (オブザーバが再構築を検出可能にする)、tombstone を破棄する
        // (新しい base ビューは生存エッジのみを含むため)。
        var epochTenant = _container.OpenTenant(AdjacencyContainer.EpochTenant, PageKind.Header);
        AdjacencyEpoch newEpoch = AdjacencyEpoch.Open(epochTenant);
        newEpoch.ResetAfterCompact(newBaseHwm);
        _edgeDeltas?.Reset();
        _edgeDeltaHeads?.ReloadMeta();
        _edgeDeltas?.ReloadMeta();
        _edgeStore.RebuildLocators();
        AdjacencyContainer.WriteDescriptor(
            adjData,
            AdjacencyContainer.KindSegment,
            payloadSpec);
        // CompactAdjacency は tx 外なので、再構築したページを durable 化する。
        _container.Flush();
        CompactAdjacencyPhaseInjector?.Invoke(CompactAdjacencyPhase.AfterFinalDescriptorFlushed);
        IAdjacencySegmentStore newStore = new AdjacencySegmentStore(
            adjData, adjIdx, payloadSpec, newEpoch);
        _adjStore = newStore;
        _txManager.SwapAdjacencyStore(newStore, writerLeaseHeld: true);
    }

    private bool TryReadPayload(EdgeId edgeId, PayloadLaneSpec spec, out long raw)
    {
        var keyId = new PropertyKeyId(spec.PropertyKeyId);
        var owner = EntityRef.From(edgeId);
        var firstPropertyRef = _edgeStore.Read(edgeId).FirstPropertyRef;
        var propEnum = _propStore.Enumerate(owner, firstPropertyRef);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId &&
                TryEncodePayload(prop.Value, spec, out raw))
            {
                return true;
            }
        }

        raw = spec.DefaultRaw;
        return false;
    }

    private Dictionary<long, long> CaptureExistingPayloads(
        IReadOnlyList<(long Id, long Src, long Tgt, int TypeId)> live)
    {
        var result = new Dictionary<long, long>();
        if (_adjStore is not IAdjacencyPayloadView)
            return result;

        var seenSources = new HashSet<long>();
        foreach (var (_, src, _, _) in live)
        {
            if (!seenSources.Add(src))
                continue;

            using var cursor = _adjStore.OpenCursor(new VertexId(src), Direction.Outgoing, null);
            while (cursor.MoveNext())
            {
                var edgeId = cursor.Edge;
                if (!_adjStore.IsTombstoned(edgeId))
                    result[edgeId.Sequence] = cursor.WeightRaw;
            }
        }

        return result;
    }

    private static bool TryEncodePayload(in PropertyValue value, PayloadLaneSpec spec, out long raw)
    {
        switch (spec.Kind)
        {
            case PayloadKind.Int64:
                if (value.Type is PropertyValueType.Int64 or PropertyValueType.Int32 or PropertyValueType.Bool)
                {
                    raw = value.Int64Value;
                    return true;
                }
                break;
            case PayloadKind.Double:
                if (value.Type == PropertyValueType.Double)
                {
                    raw = BitConverter.DoubleToInt64Bits(value.DoubleValue);
                    return true;
                }
                break;
        }

        raw = spec.DefaultRaw;
        return false;
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
    ///  4. WAL を <see cref="IWriteAheadLog.FlushTo"/> で末尾までフラッシュしてから単一 WAL を複製。
    ///     データファイルを先に取り、WAL を後に取ることで、コピー中に完了した Commit とその PageImage を
    ///     コピー先の recovery が観測できる。Commit を持たない transaction の PageImage は適用されない。
    ///
    /// target の recovery 後 LSN は snapshot WAL 末尾 LSN まで進む。
    /// </summary>
    /// <summary>
    /// oldest snapshot horizon より古い dead version の物理回収と
    /// committed registry の prune。
    /// </summary>
    public VacuumReport Vacuum(VacuumOptions? options = null)
    {
        using IDisposable garbageCollectionMeasurement =
            YatagarasuTelemetry.TrackGarbageCollection();
        using var mutationLease = _txManager.AcquireMutationLease();
        // WAL を渡して、dead version 回収後の末尾連続 free page を物理 truncate する。
        // WAL の FileTruncate レコード経由で crash recovery に対する冪等再生を保証する。
        // nexus / incidence / vertex-incidence-head の実体は transaction 配線側が保持するため、
        // そこから取り出して nexus 回収を有効にする (backend は直接参照を持たない)。
        var vac = new Vacuum(
            _vertexStore, _edgeStore, _propStore,
            _txManager, _txManager.CommittedRegistry, _wal,
            _txManager.NexusStore as VersionedNexusStore,
            _txManager.IncidenceStore as IncidenceStore,
            _txManager.VertexIncidenceHeadStore);
        VacuumReport report = vac.Run(options);
        VacuumOptions effectiveOptions = options ?? new VacuumOptions();
        if ((effectiveOptions.Targets & VacuumTarget.Indexes) != 0)
        {
            bool dryRun = effectiveOptions.Mode == VacuumMode.DryRun;
            int retiredVectorManifests = _vectorSegments.CollectGarbage(
                report.HorizonTxId,
                dryRun);
            SegmentGarbageCollectionResult fullTextGc =
                _fullTextSegments.CollectGarbage(
                    report.HorizonTxId,
                    dryRun,
                    _indexManager.ListFullTextCatalogEntries());
            report = report with
            {
                RetiredVectorManifests = retiredVectorManifests,
                RetiredFullTextManifests = fullTextGc.RetiredManifests,
                ReclaimedFullTextArtifacts = fullTextGc.ReclaimedArtifacts,
            };
        }
        if (vac.ReclaimedEdgeSequences.Count > 0)
            _relationshipReuse.BeginAndRun(vac.ReclaimedEdgeSequences);
        // vacuum は正本の incidence slot を回収する。導出ビューは active transaction が
        // 無い同じ境界で作り直し、論理削除や abort 由来の無効 entry をまとめて除去する。
        if (report.ReclaimedNexuses > 0 || _txManager.ActiveCount == 0)
            _coMembershipStore?.Rebuild(
                _txManager.NexusStore,
                _txManager.IncidenceStore);
        // vacuum は transaction 外の maintenance mutation なので、返却前に同じ writer lease 下で
        // sharp checkpoint を完了し、回収した page/free-list/catalog state を durable にする。
        _txManager.RequestCheckpoint(writerLeaseHeld: true);
        return report;
    }

    public void CreateSnapshot(string targetFilePath, SnapshotOptions? options = null)
    {
        using var mutationLease = _txManager.AcquireMutationLease();
        ArgumentException.ThrowIfNullOrEmpty(targetFilePath);
        options ??= new SnapshotOptions();

        // snapshot ターゲットも単一ファイル (*.yata)。親ディレクトリを用意する。
        var parentDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

        _txManager.RequestCheckpoint(writerLeaseHeld: true);

        // 1. コンテナ (graph.yata = コア / 索引 / 隣接 / token / epoch を同居) を page-by-page で
        //    複製する。snapshot全体がsingle-writer mutation lease内にあるためreaderは継続できるが、
        //    writerはコピー完了まで待機する。IncludeIndexesは単一ファイルではno-op
        //    (索引はコンテナに同居するため常に含まれる)。
        CopyPagedFile(_container.Physical, targetFilePath);

        // 2. WAL を末尾までフラッシュしてから単一サイドカー *.yata-wal を複製。
        //    Drain で出される PageImage 等もここで durable になる。target を開くと recovery が
        //    この WAL を replay して整合する。
        _wal.FlushTo(_wal.CurrentLsn);
        var srcWal = _containerPath + "-wal";
        if (File.Exists(srcWal))
            CopySharedFile(srcWal, targetFilePath + "-wal");

        // 3. manifestが参照するimmutable全文segment bodyを複製する。
        //    artifact storeはappend-onlyなので、checkpoint後に増えた末尾recordを含んでも
        //    snapshot側manifestから参照されず、安全な孤児になる。
        string srcFullTextSegments = _containerPath + "-ftseg";
        if (Directory.Exists(srcFullTextSegments))
            CopyImmutableArtifactDirectory(
                srcFullTextSegments,
                targetFilePath + "-ftseg");
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
            var pageId = new Yatagarasu.Core.PageId(p);
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

    private static void CopyImmutableArtifactDirectory(string srcPath, string dstPath)
    {
        Directory.CreateDirectory(dstPath);
        foreach (string sourceFile in Directory.EnumerateFiles(
                     srcPath,
                     "*.qfts",
                     SearchOption.TopDirectoryOnly))
            CopySharedFile(
                sourceFile,
                Path.Combine(dstPath, Path.GetFileName(sourceFile)));
    }

    private bool _disposed;

    public void Dispose()
    {
        // 二重 Dispose ガード。クリーン終了処理は _physical 等へアクセスするため
        // 冪等でないので、2 回目以降は no-op にする (テストが db を二重 Dispose する経路がある)。
        if (_disposed) return;
        _disposed = true;
        _scalarIndexRebuildCancellation.Cancel();
        _vectorSegmentMergeCancellation.Cancel();
        _fullTextSegmentMergeCancellation.Cancel();
        Task? rebuildTask;
        Task? vectorMergeTask;
        Task? fullTextMergeTask;
        lock (_scalarIndexRebuildSync)
            rebuildTask = _scalarIndexRebuildTask;
        lock (_vectorSegmentMergeSync)
            vectorMergeTask = _vectorSegmentMergeTask;
        lock (_fullTextSegmentMergeSync)
            fullTextMergeTask = _fullTextSegmentMergeTask;
        try { rebuildTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        try { vectorMergeTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        try { fullTextMergeTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _scalarIndexRebuildCancellation.Dispose();
        _vectorSegmentMergeCancellation.Dispose();
        _fullTextSegmentMergeCancellation.Dispose();
        _vectorSegments.Dispose();
        _fullTextSegments.Dispose();

        // クリーン終了。アクティブ tx が無ければ全データを graph.yata へ
        // durable 化し、WAL サイドカーを削除対象にする (静止時は graph.yata のみ)。
        // ActiveCount==0 なので未コミットデータは存在せず、flush 後の graph.yata は完全。
        bool cleanShutdown = _txManager.ActiveCount == 0 && !_txManager.IsFaulted;
        if (cleanShutdown)
        {
            // clean close も manual/threshold と同じ sharp checkpoint を通す。
            _txManager.RequestCheckpoint();
            if (_wal is WriteAheadLog durableWal)
                durableWal.MarkDeleteOnDispose();
        }

        _txManager.Dispose();
        if (_adjStore is IDisposable d) d.Dispose();
        _indexManager.Dispose();
        _labelTokens.Dispose();
        _edgeTypeTokens.Dispose();
        _propKeyTokens.Dispose();
        _nexusTypeTokens.Dispose();
        _roleTokens.Dispose();
        // WAL を dispose する前にデータファイルを flush する。PagedFile.Flush() は
        // write-ahead 順序 (WAL→データ) に従うため、page manager がダーティフレームを
        // ディスクへ flush する間は WAL がまだ生きている必要がある。
        _pageManager.Dispose();
        _wal.Dispose();
    }
}

internal enum CompactAdjacencyPhase
{
    AfterDescriptorInvalidated,
    AfterRebuild,
    AfterFinalDescriptorFlushed,
}

internal enum ScalarIndexRebuildPhase
{
    AfterArtifactBuilt,
    BeforePublishCommit,
    AfterPublishCommit,
}

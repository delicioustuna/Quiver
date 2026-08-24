using System.Collections.Immutable;
using System.Text;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Text;
using Yatagarasu.Transactions;

namespace Yatagarasu.Index.FullText;

internal readonly record struct FullTextSegmentMutation(
    FullTextIndexDefinition Definition,
    EntityRef Owner,
    PropertyVersionRef PropertyVersion,
    string? Text)
{
    internal bool IsTombstone => Text is null;
}

internal sealed record FullTextSegmentBuildSource(
    string IndexName,
    long ManifestGeneration,
    FullTextIndexDefinition Definition);

internal sealed record FullTextSegmentBuildArtifact(
    string IndexName,
    long SourceManifestGeneration,
    long SourceCommittedHighWater,
    FullTextIndexDefinition Definition,
    ImmutableFullTextSegment Segment,
    FullTextSegmentArtifactRef Artifact);

/// <summary>
/// commit-local deltaとlease外で構築したimmutable artifactを、
/// transaction IDでversion化したmanifestから参照する全文derived index。
/// </summary>
internal sealed class FullTextSegmentIndex : IDisposable
{
    internal static Action<FullTextArtifactPhase>? ArtifactPhaseInjector;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IndexState> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<FullTextSegmentMutation>> _pending = [];
    private readonly Func<string, ITokenizer> _resolveTokenizer;
    private readonly Action<string> _markRebuildRequired;
    private readonly LabelTokenStore _labels;
    private readonly EdgeTypeTokenStore _edgeTypes;
    private readonly NexusTypeTokenStore _nexusTypes;
    private readonly PropertyKeyTokenStore _propertyKeys;
    private readonly FullTextSegmentArtifactStore _artifacts;
    private long _primaryFallbackScanCount;
    private bool _disposed;

    internal FullTextSegmentIndex(
        string? artifactPath,
        Func<string, ITokenizer> resolveTokenizer,
        Action<string> markRebuildRequired,
        LabelTokenStore labels,
        EdgeTypeTokenStore edgeTypes,
        NexusTypeTokenStore nexusTypes,
        PropertyKeyTokenStore propertyKeys)
    {
        _resolveTokenizer = resolveTokenizer;
        _markRebuildRequired = markRebuildRequired;
        _labels = labels;
        _edgeTypes = edgeTypes;
        _nexusTypes = nexusTypes;
        _propertyKeys = propertyKeys;
        _artifacts = new FullTextSegmentArtifactStore(artifactPath);
    }

    internal Action? MergeRequested { get; set; }
    internal Action<string, IndexLifecycleState>? CatalogStateChanged { get; set; }
    internal long PrimaryFallbackScanCountForTest
        => Interlocked.Read(ref _primaryFallbackScanCount);

    internal void RegisterPending(
        long transactionId,
        List<FullTextSegmentMutation> mutations)
    {
        lock (_gate)
            _pending[transactionId] = mutations;
    }

    internal void DiscardPending(long transactionId)
    {
        lock (_gate)
        {
            _pending.Remove(transactionId);
            RollbackManifestCore(transactionId);
        }
    }

    internal void InvalidateAll()
    {
        lock (_gate)
        {
            foreach (IndexState state in _indexes.Values)
                state.Dispose();
            _indexes.Clear();
        }
    }

    internal void PrepareCommit(
        ITransaction transaction,
        IReadOnlyList<FullTextSegmentMutation> mutations)
    {
        if (mutations.Count == 0)
            return;

        var prepared = new List<PreparedManifest>();
        foreach (IGrouping<string, FullTextSegmentMutation> group in mutations.GroupBy(
                     static mutation => mutation.Definition.Name,
                     StringComparer.Ordinal))
        {
            FullTextSegmentMutation[] entries = group.ToArray();
            FullTextIndexDefinition definition = entries[0].Definition;
            if (!transaction.Indexes.TryGetFullTextDefinition(
                    definition.Name,
                    out FullTextCatalogEntry catalog))
                continue;

            var delta = new ImmutableFullTextSegment(
                entries,
                ResolveTokenizer(definition));
            FullTextSegmentArtifactRef artifact;
            try
            {
                artifact = _artifacts.Append(delta);
                ArtifactPhaseInjector?.Invoke(
                    FullTextArtifactPhase.AfterBodyFsync);
            }
            catch
            {
                delta.Dispose();
                throw;
            }

            PreparedManifest candidate;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                IndexState state = GetOrCreateState(definition, catalog.Manifest);
                if (!DefinitionEquivalent(state.Current.Definition, definition))
                {
                    state.Dispose();
                    state = new IndexState(definition);
                    _indexes[definition.Name] = state;
                }

                ManifestVersion previous = state.Current;
                candidate = new(
                    definition.Name,
                    previous.Generation,
                    new(
                        previous.Generation + 1,
                        transaction.Id.Value,
                        null,
                        definition,
                        previous.Segments.Add(delta),
                        previous.Artifacts.Add(artifact),
                        previous.CoversPrimarySnapshot,
                        previous.SourceCommittedHighWater));
            }

            transaction.Indexes.UpdateFullTextManifest(
                definition.Name,
                Encode(candidate.Manifest),
                catalog.State);
            ArtifactPhaseInjector?.Invoke(
                FullTextArtifactPhase.AfterManifestStaged);
            prepared.Add(candidate);
        }

        transaction.OnCommitted(() => PublishPrepared(transaction.Id.Value, prepared));
    }

    private void PublishPrepared(
        long committedTransactionId,
        IReadOnlyList<PreparedManifest> prepared)
    {
        bool requestMerge = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Remove(committedTransactionId);
            foreach (PreparedManifest candidate in prepared)
            {
                FullTextSegmentPolicy policy =
                    candidate.Manifest.Definition.SegmentPolicy ?? new();
                int deltaEntries = candidate.Manifest.Segments.Sum(
                    static segment => segment.EntryCount);
                int tombstones = candidate.Manifest.Segments.Sum(
                    static segment => segment.TombstoneCount);
                requestMerge |= !candidate.Manifest.CoversPrimarySnapshot
                    || deltaEntries >= policy.MaximumDeltaEntries
                    || candidate.Manifest.Segments.Length > policy.MaximumSegments
                    || deltaEntries > 0
                    && (double)tombstones / deltaEntries > policy.MaximumTombstoneRatio;

                if (!_indexes.TryGetValue(candidate.IndexName, out IndexState? state)
                    || state.Current.Generation != candidate.SourceGeneration)
                    continue;
                ManifestVersion previous = state.Current;
                state.History.Add(previous with { Xmax = committedTransactionId });
                state.Current = candidate.Manifest;
            }
        }

        if (requestMerge)
            MergeRequested?.Invoke();
    }

    internal FullTextSegmentSnapshot Open(
        ITransaction transaction,
        FullTextIndexDefinition definition)
    {
        ManifestVersion? manifest;
        bool primaryIsComplete = false;
        bool hasPending;
        lock (_gate)
        {
            IndexState state = GetOrCreateState(definition);
            manifest = SelectVisibleManifest(state, transaction.Snapshot);
            hasPending = _pending.ContainsKey(transaction.Id.Value);
            if (manifest is { CoversPrimarySnapshot: true }
                && !hasPending
                && state.Materialized.TryGetValue(
                    manifest.Generation,
                    out FullTextSegmentSnapshot? cached))
                return cached;
        }

        if (manifest is null || !manifest.CoversPrimarySnapshot)
        {
            Interlocked.Increment(ref _primaryFallbackScanCount);
            IReadOnlyList<FullTextSegmentMutation> primary = ScanPrimary(
                transaction,
                definition,
                _labels,
                _edgeTypes,
                _nexusTypes,
                _propertyKeys);
            var rebuilt = new ImmutableFullTextSegment(
                primary,
                ResolveTokenizer(definition));
            manifest = new(
                0,
                TransactionId.Bootstrap.Value,
                null,
                definition,
                [rebuilt],
                [],
                CoversPrimarySnapshot: true,
                transaction.Snapshot.CommittedHighWater);
            primaryIsComplete = true;
            MergeRequested?.Invoke();
        }

        ImmutableArray<ImmutableFullTextSegment> visibleSegments =
            manifest?.Segments ?? [];
        lock (_gate)
        {
            if (!primaryIsComplete
                && _pending.TryGetValue(transaction.Id.Value, out var pending))
            {
                FullTextSegmentMutation[] matching = pending
                    .Where(mutation => mutation.Definition.Name == definition.Name)
                    .ToArray();
                if (matching.Length > 0)
                {
                    visibleSegments = visibleSegments.Add(new ImmutableFullTextSegment(
                        matching,
                        ResolveTokenizer(definition)));
                }
            }
        }

        var result = new FullTextSegmentSnapshot(
            manifest?.Definition ?? definition,
            visibleSegments,
            ResolveTokenizer(definition));
        if (!primaryIsComplete && !hasPending && manifest is not null)
        {
            lock (_gate)
            {
                IndexState state = GetOrCreateState(definition);
                if (!state.Materialized.TryGetValue(
                        manifest.Generation,
                        out FullTextSegmentSnapshot? cached))
                {
                    state.Materialized.Add(manifest.Generation, result);
                }
                else
                {
                    result = cached;
                }
            }
        }
        return result;
    }

    internal bool TryOpen(
        ITransaction transaction,
        string indexName,
        out FullTextSegmentSnapshot snapshot)
    {
        if (!transaction.Indexes.TryGetFullTextDefinition(
                indexName,
                out FullTextCatalogEntry catalog))
        {
            snapshot = null!;
            return false;
        }
        FullTextIndexDefinition definition = FromCatalog(catalog);
        lock (_gate)
        {
            IndexState state = GetOrCreateState(definition, catalog.Manifest);
            if (catalog.State != IndexLifecycleState.Ready)
                state.Current = state.Current with
                {
                    CoversPrimarySnapshot = false,
                };
        }
        snapshot = Open(transaction, definition);
        return true;
    }

    internal static FullTextIndexDefinition FromCatalog(FullTextCatalogEntry catalog)
        => FullTextDefinitionCodec.Decode(
            catalog.Name,
            catalog.Target,
            catalog.PropertyKey,
            catalog.TokenizerId);

    internal IReadOnlyList<FullTextSegmentBuildSource> CaptureBuildSources()
    {
        lock (_gate)
        {
            var result = new List<FullTextSegmentBuildSource>();
            foreach ((string name, IndexState state) in _indexes)
            {
                FullTextSegmentPolicy policy = state.Current.Definition.SegmentPolicy ?? new();
                int entryCount = state.Current.Segments.Sum(static segment => segment.EntryCount);
                int tombstones = state.Current.Segments.Sum(static segment => segment.TombstoneCount);
                if (state.Current.CoversPrimarySnapshot
                    && state.Current.Segments.Length <= policy.MaximumSegments
                    && entryCount < policy.MaximumDeltaEntries
                    && (entryCount == 0
                        || (double)tombstones / entryCount <= policy.MaximumTombstoneRatio))
                    continue;
                result.Add(new(name, state.Current.Generation, state.Current.Definition));
            }
            return result;
        }
    }

    internal FullTextSegmentBuildArtifact Build(
        FullTextSegmentBuildSource source,
        IReadOnlyList<FullTextSegmentMutation> primary,
        long sourceCommittedHighWater)
    {
        var segment = new ImmutableFullTextSegment(
            primary,
            ResolveTokenizer(source.Definition));
        try
        {
            FullTextSegmentArtifactRef artifact = _artifacts.Append(segment);
            return new(
                source.IndexName,
                source.ManifestGeneration,
                sourceCommittedHighWater,
                source.Definition,
                segment,
                artifact);
        }
        catch
        {
            segment.Dispose();
            throw;
        }
    }

    internal bool TryPrepareMerge(
        ITransaction transaction,
        FullTextSegmentBuildArtifact artifact)
    {
        PreparedManifest prepared;
        lock (_gate)
        {
            if (!transaction.Indexes.TryGetFullTextDefinition(
                    artifact.IndexName,
                    out FullTextCatalogEntry catalog))
                return false;
            IndexState state = GetOrCreateState(
                artifact.Definition,
                catalog.Manifest);
            if (state.Current.Generation != artifact.SourceManifestGeneration
                || !DefinitionEquivalent(state.Current.Definition, artifact.Definition)
                || !DefinitionEquivalent(FromCatalog(catalog), artifact.Definition))
                return false;

            ManifestVersion previous = state.Current;
            prepared = new(
                artifact.IndexName,
                previous.Generation,
                new(
                    previous.Generation + 1,
                    transaction.Id.Value,
                    null,
                    artifact.Definition,
                    [artifact.Segment],
                    [artifact.Artifact],
                    CoversPrimarySnapshot: true,
                    artifact.SourceCommittedHighWater));
        }
        transaction.Indexes.UpdateFullTextManifest(
            artifact.IndexName,
            Encode(prepared.Manifest),
            IndexLifecycleState.Ready);
        transaction.OnCommitted(
            () => PublishPreparedMerge(transaction.Id.Value, prepared));
        transaction.OnRolledBack(
            () => RollbackPreparedMerge(transaction.Id.Value));
        return true;
    }

    private void RollbackPreparedMerge(long transactionId)
    {
        lock (_gate)
            RollbackManifestCore(transactionId);
    }

    private void RollbackManifestCore(long transactionId)
    {
        foreach (IndexState state in _indexes.Values)
        {
            if (state.Current.Xmin != transactionId
                || state.History.Count == 0
                || state.History[^1].Xmax != transactionId)
                continue;
            state.Current = state.History[^1] with { Xmax = null };
            state.History.RemoveAt(state.History.Count - 1);
            state.Materialized.Clear();
        }
    }

    private void PublishPreparedMerge(
        long committedTransactionId,
        PreparedManifest prepared)
    {
        bool published = false;
        lock (_gate)
        {
            if (!_indexes.TryGetValue(prepared.IndexName, out IndexState? state)
                || state.Current.Generation != prepared.SourceGeneration)
                return;
            ManifestVersion previous = state.Current;
            state.History.Add(previous with { Xmax = committedTransactionId });
            state.Current = prepared.Manifest;
            published = true;
        }
        if (published)
            CatalogStateChanged?.Invoke(
                prepared.IndexName,
                IndexLifecycleState.Ready);
    }

    internal static IReadOnlyList<FullTextSegmentMutation> ScanPrimary(
        ITransaction transaction,
        FullTextIndexDefinition definition,
        LabelTokenStore labels,
        EdgeTypeTokenStore edgeTypes,
        NexusTypeTokenStore nexusTypes,
        PropertyKeyTokenStore propertyKeys)
    {
        if (!propertyKeys.TryGet(definition.Target.PropertyKey, out PropertyKeyId keyId))
            return [];

        var result = new List<FullTextSegmentMutation>();
        switch (definition.Target.OwnerKind)
        {
            case PropertyOwnerKind.Vertex:
                foreach (VertexId id in transaction.Vertices.Scan())
                {
                    using VertexReadHandle owner = transaction.Vertices.Read(id);
                    if (!owner.InUse
                        || definition.Target.Scope is { } scope
                            && labels.GetName(owner.Label) != scope)
                        continue;
                    AddPrimary(
                        result,
                        definition,
                        EntityRef.From(owner.Id),
                        transaction.Vertices.EnumerateProperties(id, transaction.Properties),
                        keyId);
                }
                break;
            case PropertyOwnerKind.Edge:
                foreach (EdgeId id in transaction.Edges.Scan())
                {
                    EdgeReadHandle owner = transaction.Edges.Read(id);
                    if (!owner.InUse
                        || definition.Target.Scope is { } scope
                            && edgeTypes.GetName(owner.Type) != scope)
                        continue;
                    AddPrimary(
                        result,
                        definition,
                        EntityRef.From(owner.Id),
                        transaction.Edges.EnumerateProperties(id, transaction.Properties),
                        keyId);
                }
                break;
            case PropertyOwnerKind.Nexus:
                foreach (NexusId id in transaction.Nexuses.Scan())
                {
                    using NexusReadHandle owner = transaction.Nexuses.Read(id);
                    if (!owner.InUse
                        || definition.Target.Scope is { } scope
                            && nexusTypes.GetName(owner.Type) != scope)
                        continue;
                    AddPrimary(
                        result,
                        definition,
                        EntityRef.From(owner.Id),
                        transaction.Nexuses.EnumerateProperties(id, transaction.Properties),
                        keyId);
                }
                break;
        }
        return result;
    }

    private static void AddPrimary(
        ICollection<FullTextSegmentMutation> result,
        FullTextIndexDefinition definition,
        EntityRef owner,
        PropertyCursor properties,
        PropertyKeyId keyId)
    {
        while (properties.MoveNext())
        {
            PropertyEntry property = properties.Current;
            if (property.KeyId != keyId
                || property.Value.Type != PropertyValueType.String)
                continue;
            result.Add(new(
                definition,
                owner,
                properties.CurrentVersion,
                Encoding.UTF8.GetString(property.Value.Utf8StringValue)));
            return;
        }
    }

    private IndexState GetOrCreateState(
        FullTextIndexDefinition definition,
        string? encodedManifest = null)
    {
        if (_indexes.TryGetValue(definition.Name, out IndexState? state))
        {
            SynchronizeFromCatalog(state, definition, encodedManifest);
            return state;
        }
        state = new(definition);
        SynchronizeFromCatalog(state, definition, encodedManifest);
        _indexes.Add(definition.Name, state);
        return state;
    }

    private void SynchronizeFromCatalog(
        IndexState state,
        FullTextIndexDefinition definition,
        string? encodedManifest)
    {
        if (!string.IsNullOrEmpty(encodedManifest))
        {
            try
            {
                FullTextDurableManifest durable =
                    FullTextSegmentArtifactStore.DecodeManifest(encodedManifest);
                if (durable.Generation <= state.Current.Generation)
                    return;
                var segments = ImmutableArray.CreateBuilder<ImmutableFullTextSegment>(
                    durable.Segments.Length);
                foreach (FullTextSegmentArtifactRef artifact in durable.Segments)
                    segments.Add(_artifacts.Read(artifact));
                ManifestVersion next = new(
                    durable.Generation,
                    durable.Xmin,
                    durable.Xmax,
                    definition,
                    segments.MoveToImmutable(),
                    durable.Segments,
                    durable.CoversPrimarySnapshot,
                    durable.SourceCommittedHighWater);
                state.History.Add(state.Current with { Xmax = durable.Xmin });
                state.Current = next;
                state.Materialized.Clear();
            }
            catch (CorruptionException)
            {
                _markRebuildRequired(definition.Name);
                CatalogStateChanged?.Invoke(
                    definition.Name,
                    IndexLifecycleState.RebuildRequired);
                if (state.Current.Generation == 0)
                    state.Current = state.Current with
                    {
                        CoversPrimarySnapshot = false,
                    };
            }
        }
    }

    private static string Encode(ManifestVersion manifest)
        => FullTextSegmentArtifactStore.EncodeManifest(new(
            manifest.Generation,
            manifest.Xmin,
            manifest.Xmax,
            manifest.SourceCommittedHighWater,
            manifest.CoversPrimarySnapshot,
            manifest.Artifacts));

    private ITokenizer ResolveTokenizer(FullTextIndexDefinition definition)
    {
        try
        {
            return _resolveTokenizer(definition.TokenizerId);
        }
        catch (KeyNotFoundException) when (definition.Filters is { Count: > 0 })
        {
            string baseTokenizerId = definition.TokenizerId.Split('+', 2)[0];
            return new FilteredTokenizer(
                _resolveTokenizer(baseTokenizerId),
                [.. definition.Filters]);
        }
    }

    private static ManifestVersion? SelectVisibleManifest(
        IndexState state,
        in SnapshotState snapshot)
    {
        if (IsVisible(state.Current, snapshot))
            return state.Current;
        for (int i = state.History.Count - 1; i >= 0; i--)
            if (IsVisible(state.History[i], snapshot))
                return state.History[i];
        return null;
    }

    private static bool IsVisible(ManifestVersion manifest, in SnapshotState snapshot)
        => IsTransactionVisible(manifest.Xmin, snapshot)
            && (manifest.Xmax is null
                || !IsTransactionVisible(manifest.Xmax.Value, snapshot));

    private static bool IsTransactionVisible(long transactionId, in SnapshotState snapshot)
        => transactionId <= snapshot.CommittedHighWater
            && !snapshot.AbortedGaps.Contains(transactionId);

    internal static bool DefinitionEquivalent(
        FullTextIndexDefinition left,
        FullTextIndexDefinition right)
        => left.Name == right.Name
            && left.Target == right.Target
            && left.TokenizerId == right.TokenizerId
            && FullTextDefinitionCodec.EncodeFilters(left.Filters)
                == FullTextDefinitionCodec.EncodeFilters(right.Filters)
            && left.K1 == right.K1
            && left.B == right.B
            && left.SegmentPolicy == right.SegmentPolicy;

    internal SegmentGarbageCollectionResult CollectGarbage(
        long horizonTransactionId,
        bool dryRun,
        IEnumerable<FullTextCatalogEntry> catalogEntries)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (FullTextCatalogEntry catalog in catalogEntries)
                GetOrCreateState(FromCatalog(catalog), catalog.Manifest);
            int retiredManifests = 0;
            var retainedArtifacts = new HashSet<Guid>();
            foreach (IndexState state in _indexes.Values)
            {
                ManifestVersion[] retired = state.History
                    .Where(manifest => manifest.Xmax < horizonTransactionId)
                    .ToArray();
                retiredManifests += retired.Length;

                foreach (FullTextSegmentArtifactRef artifact in state.Current.Artifacts)
                    retainedArtifacts.Add(artifact.ArtifactId);
                foreach (ManifestVersion manifest in state.History)
                {
                    if (manifest.Xmax < horizonTransactionId)
                        continue;
                    foreach (FullTextSegmentArtifactRef artifact in manifest.Artifacts)
                        retainedArtifacts.Add(artifact.ArtifactId);
                }

                if (dryRun || retired.Length == 0)
                    continue;

                // manifest body は immutable でも、旧 snapshot の検索が終了する前に消すと
                // 同じ transaction 内の再検索が再現不能になる。current だけを正本として即時削除せず、
                // SnapshotRegistry が固定した horizon を越えた世代だけを退役させる。
                state.History.RemoveAll(manifest => manifest.Xmax < horizonTransactionId);
                foreach (long generation in retired.Select(static manifest => manifest.Generation))
                    state.Materialized.Remove(generation);

                var retainedSegments = new HashSet<ImmutableFullTextSegment>(
                    ReferenceEqualityComparer.Instance);
                foreach (ImmutableFullTextSegment segment in state.Current.Segments)
                    retainedSegments.Add(segment);
                foreach (ManifestVersion manifest in state.History)
                    foreach (ImmutableFullTextSegment segment in manifest.Segments)
                        retainedSegments.Add(segment);
                var disposed = new HashSet<ImmutableFullTextSegment>(
                    ReferenceEqualityComparer.Instance);
                foreach (ManifestVersion manifest in retired)
                    foreach (ImmutableFullTextSegment segment in manifest.Segments)
                        if (!retainedSegments.Contains(segment) && disposed.Add(segment))
                            segment.Dispose();
            }

            int reclaimedArtifacts = dryRun
                ? _artifacts.CountGarbage(retainedArtifacts)
                : _artifacts.CollectGarbage(retainedArtifacts);
            return new(retiredManifests, reclaimedArtifacts);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (IndexState state in _indexes.Values)
                state.Dispose();
            _indexes.Clear();
            _pending.Clear();
            _artifacts.Dispose();
        }
    }

    private sealed class IndexState(FullTextIndexDefinition definition) : IDisposable
    {
        internal ManifestVersion Current { get; set; } = new(
            0,
            TransactionId.Bootstrap.Value,
            null,
            definition,
            [],
            [],
            CoversPrimarySnapshot: false,
            TransactionId.Bootstrap.Value);

        internal List<ManifestVersion> History { get; } = [];
        internal Dictionary<long, FullTextSegmentSnapshot> Materialized { get; } = [];

        internal void DisposeSegments()
        {
            var disposed = new HashSet<ImmutableFullTextSegment>(
                ReferenceEqualityComparer.Instance);
            foreach (ImmutableFullTextSegment segment in Current.Segments)
                if (disposed.Add(segment))
                    segment.Dispose();
            foreach (ManifestVersion manifest in History)
                foreach (ImmutableFullTextSegment segment in manifest.Segments)
                    if (disposed.Add(segment))
                        segment.Dispose();
        }

        public void Dispose()
        {
            DisposeSegments();
            Materialized.Clear();
        }
    }

    private sealed record ManifestVersion(
        long Generation,
        long Xmin,
        long? Xmax,
        FullTextIndexDefinition Definition,
        ImmutableArray<ImmutableFullTextSegment> Segments,
        ImmutableArray<FullTextSegmentArtifactRef> Artifacts,
        bool CoversPrimarySnapshot,
        long SourceCommittedHighWater);

    private sealed record PreparedManifest(
        string IndexName,
        long SourceGeneration,
        ManifestVersion Manifest);
}

internal readonly record struct SegmentGarbageCollectionResult(
    int RetiredManifests,
    int ReclaimedArtifacts);

internal enum FullTextArtifactPhase
{
    AfterBodyFsync,
    AfterManifestStaged,
}

internal readonly record struct FullTextStoredEntry(
    long Owner,
    PropertyVersionRef PropertyVersion,
    bool IsTombstone);

internal sealed class ImmutableFullTextSegment : IDisposable
{
    private readonly FullTextStoredEntry[] _entries;
    private readonly Dictionary<string, List<(long Owner, int Tf)>> _postings;
    private readonly Dictionary<long, int> _norms;

    internal ImmutableFullTextSegment(
        IReadOnlyList<FullTextSegmentMutation> entries,
        ITokenizer tokenizer)
    {
        var latest = new Dictionary<long, FullTextSegmentMutation>();
        foreach (FullTextSegmentMutation entry in entries)
        {
            long owner = EntityRef.Pack(
                entry.Owner.Kind,
                entry.Owner.Sequence,
                entry.Owner.Generation);
            latest[owner] = entry;
        }

        _entries = new FullTextStoredEntry[latest.Count];
        _postings = new(StringComparer.Ordinal);
        _norms = [];
        int entryIndex = 0;
        foreach ((long owner, FullTextSegmentMutation entry) in latest)
        {
            _entries[entryIndex] = new(
                owner,
                entry.PropertyVersion,
                entry.IsTombstone);
            entryIndex++;
            if (entry.Text is null)
                continue;

            var sink = new TfSink();
            tokenizer.Tokenize(entry.Text, sink);
            foreach ((string term, int tf) in sink.Frequencies)
            {
                if (!_postings.TryGetValue(term, out List<(long, int)>? list))
                    _postings.Add(term, list = []);
                list.Add((owner, Math.Min(tf, ushort.MaxValue)));
            }
            _norms[owner] = tokenizer is INormTokenCounter counter
                ? counter.CountNormTokens(entry.Text)
                : sink.Total;
        }

        foreach (List<(long Owner, int Tf)> postings in _postings.Values)
            postings.Sort(static (left, right) => left.Owner.CompareTo(right.Owner));
    }

    internal ImmutableFullTextSegment(
        FullTextStoredEntry[] entries,
        Dictionary<string, List<(long Owner, int Tf)>> postings,
        Dictionary<long, int> norms)
    {
        _entries = entries;
        _postings = postings;
        _norms = norms;
    }

    internal int EntryCount => _entries.Length;
    internal int TombstoneCount => _entries.Count(static entry => entry.IsTombstone);
    internal IReadOnlyList<FullTextStoredEntry> Entries => _entries;
    internal IReadOnlyDictionary<string, List<(long Owner, int Tf)>> Postings => _postings;
    internal IReadOnlyDictionary<long, int> Norms => _norms;

    public void Dispose()
    {
    }

    private sealed class TfSink : ITokenSink
    {
        internal Dictionary<string, int> Frequencies { get; } = new(StringComparer.Ordinal);
        internal int Total { get; private set; }

        public void Accept(ReadOnlySpan<char> token)
        {
            Total++;
            string value = token.ToString();
            Frequencies[value] = Frequencies.GetValueOrDefault(value) + 1;
        }
    }
}

internal sealed class FullTextSegmentSnapshot
{
    private readonly Dictionary<string, List<(long EntityId, int Tf)>> _postings =
        new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _norms = [];
    private readonly Dictionary<long, PropertyVersionRef> _propertyVersions = [];

    internal FullTextSegmentSnapshot(
        FullTextIndexDefinition definition,
        ImmutableArray<ImmutableFullTextSegment> segments,
        ITokenizer tokenizer)
    {
        Definition = definition;
        Tokenizer = tokenizer;
        var latest = new Dictionary<long, (int SegmentIndex, FullTextStoredEntry Entry)>();
        for (int segmentIndex = segments.Length - 1; segmentIndex >= 0; segmentIndex--)
        {
            IReadOnlyList<FullTextStoredEntry> entries = segments[segmentIndex].Entries;
            for (int entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
            {
                FullTextStoredEntry entry = entries[entryIndex];
                latest.TryAdd(entry.Owner, (segmentIndex, entry));
            }
        }

        foreach ((long owner, (int _, FullTextStoredEntry entry)) in latest)
        {
            if (entry.IsTombstone)
                continue;
            _propertyVersions[owner] = entry.PropertyVersion;
        }

        for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            ImmutableFullTextSegment segment = segments[segmentIndex];
            foreach ((long owner, int norm) in segment.Norms)
            {
                if (latest.TryGetValue(owner, out var selected)
                    && selected.SegmentIndex == segmentIndex
                    && !selected.Entry.IsTombstone)
                    _norms[owner] = norm;
            }
            foreach ((string term, List<(long Owner, int Tf)> postings) in segment.Postings)
            {
                foreach ((long owner, int tf) in postings)
                {
                    if (!latest.TryGetValue(owner, out var selected)
                        || selected.SegmentIndex != segmentIndex
                        || selected.Entry.IsTombstone)
                        continue;
                    if (!_postings.TryGetValue(term, out List<(long, int)>? list))
                        _postings.Add(term, list = []);
                    list.Add((owner, tf));
                }
            }
        }

        foreach (List<(long EntityId, int Tf)> postings in _postings.Values)
            postings.Sort(static (left, right) => left.EntityId.CompareTo(right.EntityId));
    }

    internal FullTextIndexDefinition Definition { get; }
    internal ITokenizer Tokenizer { get; }
    internal string TokenizerId => Definition.TokenizerId;
    internal long DocumentCount => _norms.Count;

    internal (long DocCount, long TotalTokens) NormsSummary()
        => (_norms.Count, _norms.Values.Sum(static value => (long)value));

    internal List<(long EntityId, int Tf)> GetPostings(string term)
        => _postings.TryGetValue(term, out List<(long EntityId, int Tf)>? postings)
            ? [.. postings]
            : [];

    internal bool TryGetDocLength(long entityId, out int docLen)
        => _norms.TryGetValue(entityId, out docLen);

    internal bool IsVisibleVertexCandidate(long packed, ITransaction transaction)
    {
        if (EntityRef.UnpackKind(packed) != EntityKind.Vertex
            || !_propertyVersions.TryGetValue(
                packed,
                out PropertyVersionRef propertyVersion))
            return false;

        var id = VertexId.Create(
            EntityRef.UnpackSequence(packed),
            EntityRef.UnpackGeneration(packed));
        using VertexReadHandle vertex = transaction.Vertices.Read(id);
        if (!vertex.InUse || vertex.Id != id)
            return false;
        EntityRef owner = EntityRef.From(id);
        PropertyVersionRecord property =
            transaction.Properties.Read(owner, propertyVersion);
        return property.InUse
            && property.Version == propertyVersion
            && property.Address.Owner == owner;
    }

    internal HashSet<string> ExpandPrefix(ReadOnlySpan<byte> prefixUtf8)
    {
        string prefix = Encoding.UTF8.GetString(prefixUtf8);
        return _postings.Keys
            .Where(term => term.StartsWith(prefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    internal HashSet<string> ExpandFuzzy(ReadOnlySpan<byte> termUtf8, int maxEditDistance)
    {
        string term = Encoding.UTF8.GetString(termUtf8);
        return _postings.Keys
            .Where(candidate => FullTextTextAlgorithms.LevenshteinDistance(term, candidate)
                <= maxEditDistance)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal (
        Dictionary<string, (int Df, int MaxTf)> Terms,
        int MinDocLen,
        long DocCount,
        long TotalTokens) CollectTermStats()
    {
        var terms = new Dictionary<string, (int Df, int MaxTf)>(StringComparer.Ordinal);
        foreach ((string term, List<(long EntityId, int Tf)> postings) in _postings)
            terms[term] = (postings.Count, postings.Max(static posting => posting.Tf));
        return (
            terms,
            _norms.Count == 0 ? 0 : _norms.Values.Min(),
            _norms.Count,
            _norms.Values.Sum(static value => (long)value));
    }

    internal FullTextPostingsCursor OpenPostingsCursor(ReadOnlySpan<byte> termUtf8)
        => new(GetPostings(Encoding.UTF8.GetString(termUtf8)));

}

internal sealed class FullTextPostingsCursor(IReadOnlyList<(long EntityId, int Tf)> postings)
{
    private int _position = -1;

    internal bool Exhausted => _position >= postings.Count;
    internal long CurrentEid { get; private set; }
    internal int CurrentTf { get; private set; }

    internal bool MoveNext()
    {
        if (_position + 1 >= postings.Count)
        {
            _position = postings.Count;
            return false;
        }
        _position++;
        Decode();
        return true;
    }

    internal bool SeekTo(long entityId)
    {
        int low = Math.Max(0, _position);
        int high = postings.Count - 1;
        int found = postings.Count;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (postings[middle].EntityId >= entityId)
            {
                found = middle;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }
        if (found >= postings.Count)
        {
            _position = postings.Count;
            return false;
        }
        _position = found;
        Decode();
        return true;
    }

    private void Decode()
    {
        CurrentEid = postings[_position].EntityId;
        CurrentTf = postings[_position].Tf;
    }
}

internal static class FullTextTextAlgorithms
{
    internal static int LevenshteinDistance(string source, string target)
    {
        if (source.Length == 0)
            return target.Length;
        if (target.Length == 0)
            return source.Length;
        var previous = new int[target.Length + 1];
        for (int j = 0; j <= target.Length; j++)
            previous[j] = j;
        for (int i = 1; i <= source.Length; i++)
        {
            int diagonal = previous[0];
            previous[0] = i;
            for (int j = 1; j <= target.Length; j++)
            {
                int saved = previous[j];
                int cost = source[i - 1] == target[j - 1] ? 0 : 1;
                previous[j] = Math.Min(
                    Math.Min(previous[j] + 1, previous[j - 1] + 1),
                    diagonal + cost);
                diagonal = saved;
            }
        }
        return previous[target.Length];
    }
}

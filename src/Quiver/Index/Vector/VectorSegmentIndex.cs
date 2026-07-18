using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Index.Vector;

internal readonly record struct VectorSegmentMutation(
    VectorIndexDefinition Definition,
    EntityRef Owner,
    ulong PayloadChecksum,
    float[]? Vector)
{
    internal bool IsTombstone => Vector is null;

    internal static VectorSegmentMutation Upsert(
        VectorIndexDefinition definition,
        EntityRef owner,
        ReadOnlySpan<float> vector)
        => new(definition, owner, VectorPayloadChecksum.Compute(vector), vector.ToArray());

    internal static VectorSegmentMutation Tombstone(
        VectorIndexDefinition definition,
        EntityRef owner)
        => new(definition, owner, 0, null);
}

internal readonly record struct VectorSegmentCandidate(
    EntityRef Owner,
    ulong PayloadChecksum,
    float Score);

internal sealed record VectorSegmentSearchResult(
    IReadOnlyList<VectorSegmentCandidate> Candidates,
    bool CoversPrimarySnapshot,
    long ManifestGeneration);

internal sealed record VectorSegmentBuildSource(
    string IndexName,
    long ManifestGeneration,
    VectorIndexDefinition Definition);

internal sealed record VectorSegmentBuildArtifact(
    string IndexName,
    long SourceManifestGeneration,
    VectorIndexDefinition Definition,
    ImmutableVectorHnswSegment Segment);

/// <summary>
/// commitごとのflat deltaとlease外で構築したimmutable HNSWを、
/// transaction IDでversion化したmanifestから参照するderived index。
/// </summary>
internal sealed class VectorSegmentIndex : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IndexState> _indexes = new(StringComparer.Ordinal);
    private bool _disposed;

    internal Action? MergeRequested { get; set; }

    internal void PublishDelta(
        long committedTransactionId,
        IReadOnlyList<VectorSegmentMutation> mutations)
    {
        if (mutations.Count == 0)
            return;

        bool requestMerge = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (IGrouping<string, VectorSegmentMutation> group in mutations.GroupBy(
                         static mutation => mutation.Definition.Name,
                         StringComparer.Ordinal))
            {
                VectorSegmentMutation[] entries = group.ToArray();
                VectorIndexDefinition definition = entries[0].Definition;
                IndexState state = GetOrCreateState(definition);
                if (state.Current.Definition != definition)
                {
                    state.Dispose();
                    state = new IndexState(definition);
                    _indexes[definition.Name] = state;
                }

                var delta = new ImmutableVectorFlatSegment(entries);
                ManifestVersion previous = state.Current;
                state.History.Add(previous with { Xmax = committedTransactionId });
                state.Current = new ManifestVersion(
                    previous.Generation + 1,
                    committedTransactionId,
                    null,
                    definition,
                    previous.Segments.Add(delta),
                    previous.CoversPrimarySnapshot);

                VectorSegmentPolicy policy = definition.SegmentPolicy ?? new();
                int deltaEntries = state.Current.Segments
                    .OfType<ImmutableVectorFlatSegment>()
                    .Sum(static segment => segment.Count);
                requestMerge |= deltaEntries >= policy.MaximumDeltaEntries
                    || state.Current.Segments.Length >= policy.MaximumSegments;
            }
        }

        if (requestMerge)
            MergeRequested?.Invoke();
    }

    internal VectorSegmentSearchResult Search(
        in SnapshotState snapshot,
        VectorIndexDefinition definition,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options)
    {
        ManifestVersion? manifest;
        bool requestRebuild = false;
        lock (_gate)
        {
            if (!_indexes.TryGetValue(definition.Name, out IndexState? state))
            {
                state = new IndexState(definition);
                _indexes.Add(definition.Name, state);
                requestRebuild = true;
            }
            manifest = SelectVisibleManifest(state, snapshot);
            requestRebuild |= manifest is { CoversPrimarySnapshot: false, Segments.Length: 0 };
        }
        if (requestRebuild)
            MergeRequested?.Invoke();
        if (manifest is null || manifest.Definition != definition)
            return new([], false, 0);

        int perSegment = Math.Max(
            k,
            checked(k * VectorSearchOptionsValidator.Normalize(options).FilteredOversampleFactor));
        var candidates = new List<VectorSegmentCandidate>(
            manifest.Segments.Length * Math.Min(perSegment, 256));
        foreach (ImmutableVectorSegment segment in manifest.Segments)
            candidates.AddRange(segment.Search(query, perSegment, definition.Metric, options));

        candidates.Sort(static (left, right) =>
        {
            int score = right.Score.CompareTo(left.Score);
            if (score != 0)
                return score;
            int kind = left.Owner.Kind.CompareTo(right.Owner.Kind);
            return kind != 0
                ? kind
                : left.Owner.Value.CompareTo(right.Owner.Value);
        });
        return new(
            candidates,
            manifest.CoversPrimarySnapshot,
            manifest.Generation);
    }

    internal IReadOnlyList<VectorSegmentBuildSource> CaptureBuildSources()
    {
        lock (_gate)
        {
            var sources = new List<VectorSegmentBuildSource>();
            foreach ((string name, IndexState state) in _indexes)
            {
                VectorSegmentPolicy policy = state.Current.Definition.SegmentPolicy ?? new();
                int deltaEntries = state.Current.Segments
                    .OfType<ImmutableVectorFlatSegment>()
                    .Sum(static segment => segment.Count);
                if (state.Current.CoversPrimarySnapshot
                    && deltaEntries < policy.MaximumDeltaEntries
                    && state.Current.Segments.Length < policy.MaximumSegments)
                    continue;
                sources.Add(new(
                    name,
                    state.Current.Generation,
                    state.Current.Definition));
            }
            return sources;
        }
    }

    internal static VectorSegmentBuildArtifact Build(
        VectorSegmentBuildSource source,
        IReadOnlyList<VectorSegmentMutation> primaryEntries)
        => new(
            source.IndexName,
            source.ManifestGeneration,
            source.Definition,
            new ImmutableVectorHnswSegment(source.Definition, primaryEntries));

    internal bool TryPublishMerge(
        long committedTransactionId,
        VectorSegmentBuildArtifact artifact,
        IndexDefinition? currentDefinition)
    {
        lock (_gate)
        {
            if (!_indexes.TryGetValue(artifact.IndexName, out IndexState? state)
                || state.Current.Generation != artifact.SourceManifestGeneration
                || state.Current.Definition != artifact.Definition
                || currentDefinition != artifact.Definition)
                return false;

            ManifestVersion previous = state.Current;
            state.History.Add(previous with { Xmax = committedTransactionId });
            state.Current = new ManifestVersion(
                previous.Generation + 1,
                committedTransactionId,
                null,
                artifact.Definition,
                [artifact.Segment],
                CoversPrimarySnapshot: true);
            return true;
        }
    }

    internal static IReadOnlyList<VectorSegmentMutation> ScanPrimary(
        ITransaction transaction,
        VectorIndexDefinition definition,
        LabelTokenStore labels,
        EdgeTypeTokenStore edgeTypes,
        NexusTypeTokenStore nexusTypes,
        PropertyKeyTokenStore propertyKeys)
    {
        if (!propertyKeys.TryGet(definition.Target.PropertyKey, out PropertyKeyId keyId))
            return [];

        var result = new List<VectorSegmentMutation>();
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
        ICollection<VectorSegmentMutation> result,
        VectorIndexDefinition definition,
        EntityRef owner,
        PropertyCursor properties,
        PropertyKeyId keyId)
    {
        while (properties.MoveNext())
        {
            PropertyEntry property = properties.Current;
            if (property.KeyId != keyId
                || property.Value.Type != PropertyValueType.FloatArray
                || property.Value.FloatArrayValue.Length != definition.Dimensions)
                continue;
            result.Add(VectorSegmentMutation.Upsert(
                definition,
                owner,
                property.Value.FloatArrayValue));
            return;
        }
    }

    private IndexState GetOrCreateState(VectorIndexDefinition definition)
    {
        if (_indexes.TryGetValue(definition.Name, out IndexState? state))
            return state;
        state = new IndexState(definition);
        _indexes.Add(definition.Name, state);
        return state;
    }

    private static ManifestVersion? SelectVisibleManifest(
        IndexState state,
        in SnapshotState snapshot)
    {
        if (IsVisible(state.Current, snapshot))
            return state.Current;
        for (int i = state.History.Count - 1; i >= 0; i--)
        {
            ManifestVersion candidate = state.History[i];
            if (IsVisible(candidate, snapshot))
                return candidate;
        }
        return null;
    }

    private static bool IsVisible(ManifestVersion manifest, in SnapshotState snapshot)
        => IsTransactionVisible(manifest.Xmin, snapshot)
            && (manifest.Xmax is null
                || !IsTransactionVisible(manifest.Xmax.Value, snapshot));

    private static bool IsTransactionVisible(long transactionId, in SnapshotState snapshot)
        => transactionId <= snapshot.CommittedHighWater
            && !snapshot.AbortedGaps.Contains(transactionId);

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
        }
    }

    private sealed class IndexState(VectorIndexDefinition definition) : IDisposable
    {
        internal ManifestVersion Current { get; set; } = new(
            Generation: 0,
            Xmin: TransactionId.Bootstrap.Value,
            Xmax: null,
            Definition: definition,
            Segments: [],
            CoversPrimarySnapshot: false);

        internal List<ManifestVersion> History { get; } = [];

        public void Dispose()
        {
            var disposed = new HashSet<ImmutableVectorSegment>(
                ReferenceEqualityComparer.Instance);
            foreach (ImmutableVectorSegment segment in Current.Segments)
                if (disposed.Add(segment))
                    segment.Dispose();
            foreach (ManifestVersion manifest in History)
                foreach (ImmutableVectorSegment segment in manifest.Segments)
                    if (disposed.Add(segment))
                        segment.Dispose();
        }
    }

    private sealed record ManifestVersion(
        long Generation,
        long Xmin,
        long? Xmax,
        VectorIndexDefinition Definition,
        ImmutableArray<ImmutableVectorSegment> Segments,
        bool CoversPrimarySnapshot);
}

internal abstract class ImmutableVectorSegment : IDisposable
{
    internal abstract int Count { get; }

    internal abstract IReadOnlyList<VectorSegmentCandidate> Search(
        ReadOnlySpan<float> query,
        int k,
        DistanceMetric metric,
        VectorSearchOptions? options);

    public virtual void Dispose()
    {
    }
}

internal sealed class ImmutableVectorFlatSegment : ImmutableVectorSegment
{
    private readonly VectorSegmentMutation[] _entries;

    internal ImmutableVectorFlatSegment(IReadOnlyList<VectorSegmentMutation> entries)
        => _entries = entries
            .Select(static entry => entry with { Vector = entry.Vector?.ToArray() })
            .ToArray();

    internal override int Count => _entries.Length;

    internal override IReadOnlyList<VectorSegmentCandidate> Search(
        ReadOnlySpan<float> query,
        int k,
        DistanceMetric metric,
        VectorSearchOptions? options)
    {
        var heap = new VectorSegmentCandidateHeap(k);
        foreach (VectorSegmentMutation entry in _entries)
        {
            if (entry.Vector is null || entry.Vector.Length != query.Length)
                continue;
            heap.Offer(new(
                entry.Owner,
                entry.PayloadChecksum,
                VectorMetrics.Score(metric, query, entry.Vector)));
        }
        return heap.ToSortedArray();
    }
}

internal sealed class ImmutableVectorHnswSegment : ImmutableVectorSegment
{
    private readonly VectorSegmentMutation[] _entries;
    private readonly InMemoryPagedFile _payloadFile = new();
    private readonly InMemoryPagedFile _hnswFile = new();
    private readonly HnswIndex _hnsw;

    internal ImmutableVectorHnswSegment(
        VectorIndexDefinition definition,
        IReadOnlyList<VectorSegmentMutation> entries)
    {
        _entries = entries
            .Where(static entry => !entry.IsTombstone)
            .Select(static entry => entry with { Vector = entry.Vector!.ToArray() })
            .ToArray();
        var descriptor = new VectorIndexDescriptor(
            definition.Name,
            ToEntityKind(definition.Target.OwnerKind),
            PropertyKeyId.Invalid,
            definition.Target.Scope,
            definition.Dimensions,
            definition.Metric,
            ElementType: definition.ElementType,
            HnswM: definition.HnswM,
            HnswMMax0: definition.HnswMMax0,
            HnswMaxLayers: definition.HnswMaxLayers,
            HnswEfConstruction: definition.HnswEfConstruction,
            SegmentPolicy: definition.SegmentPolicy);
        var payload = new VectorIndexPayloadStore(
            _payloadFile,
            definition.Dimensions,
            definition.ElementType,
            new VectorPayloadCacheBudget(0));
        _hnsw = new HnswIndex(_hnswFile, payload, descriptor);
        for (int i = 0; i < _entries.Length; i++)
        {
            payload.Set(i, 0, _entries[i].Vector!);
            _hnsw.Insert(i);
        }
    }

    internal override int Count => _entries.Length;

    internal override IReadOnlyList<VectorSegmentCandidate> Search(
        ReadOnlySpan<float> query,
        int k,
        DistanceMetric metric,
        VectorSearchOptions? options)
    {
        VectorSearchResult[] hits = _hnsw.Search(
            query,
            k,
            EntityKind.Vertex,
            static (_, _) => true,
            VectorSearchOptionsValidator.Normalize(options));
        var result = new VectorSegmentCandidate[hits.Length];
        for (int i = 0; i < hits.Length; i++)
        {
            VectorSegmentMutation entry = _entries[checked((int)hits[i].Owner.Sequence)];
            result[i] = new(entry.Owner, entry.PayloadChecksum, hits[i].Score);
        }
        return result;
    }

    public override void Dispose()
    {
        _payloadFile.Dispose();
        _hnswFile.Dispose();
    }

    private static EntityKind ToEntityKind(PropertyOwnerKind ownerKind)
        => ownerKind switch
        {
            PropertyOwnerKind.Vertex => EntityKind.Vertex,
            PropertyOwnerKind.Edge => EntityKind.Edge,
            PropertyOwnerKind.Nexus => EntityKind.Nexus,
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        };
}

internal static class VectorPayloadChecksum
{
    internal static ulong Compute(ReadOnlySpan<float> vector)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(vector);
        ulong hash = 14695981039346656037UL;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

internal sealed class VectorSegmentCandidateHeap(int capacity)
{
    private readonly PriorityQueue<VectorSegmentCandidate, (float Score, long Tie)> _queue =
        new(capacity);

    internal void Offer(VectorSegmentCandidate candidate)
    {
        var priority = (candidate.Score, -candidate.Owner.Value);
        if (_queue.Count < capacity)
        {
            _queue.Enqueue(candidate, priority);
            return;
        }
        _queue.TryPeek(out _, out var worst);
        if (priority.CompareTo(worst) <= 0)
            return;
        _queue.Dequeue();
        _queue.Enqueue(candidate, priority);
    }

    internal IReadOnlyList<VectorSegmentCandidate> ToSortedArray()
        => _queue.UnorderedItems
            .Select(static item => item.Element)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Owner.Value)
            .ToArray();
}

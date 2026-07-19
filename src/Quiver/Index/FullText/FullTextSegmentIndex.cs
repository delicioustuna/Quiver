using System.Collections.Immutable;
using System.Text;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Text;
using Quiver.Transactions;

namespace Quiver.Index.FullText;

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
    FullTextIndexDefinition Definition,
    ImmutableFullTextSegment Segment);

/// <summary>
/// commit-local deltaとlease外で構築したimmutable artifactを、
/// transaction IDでversion化したmanifestから参照する全文derived index。
/// </summary>
internal sealed class FullTextSegmentIndex : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IndexState> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<FullTextSegmentMutation>> _pending = [];
    private readonly Func<string, ITokenizer> _resolveTokenizer;
    private readonly LabelTokenStore _labels;
    private readonly EdgeTypeTokenStore _edgeTypes;
    private readonly NexusTypeTokenStore _nexusTypes;
    private readonly PropertyKeyTokenStore _propertyKeys;
    private bool _disposed;

    internal FullTextSegmentIndex(
        Func<string, ITokenizer> resolveTokenizer,
        LabelTokenStore labels,
        EdgeTypeTokenStore edgeTypes,
        NexusTypeTokenStore nexusTypes,
        PropertyKeyTokenStore propertyKeys)
    {
        _resolveTokenizer = resolveTokenizer;
        _labels = labels;
        _edgeTypes = edgeTypes;
        _nexusTypes = nexusTypes;
        _propertyKeys = propertyKeys;
    }

    internal Action? MergeRequested { get; set; }

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
            _pending.Remove(transactionId);
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

    internal void PublishDelta(
        long committedTransactionId,
        IReadOnlyList<FullTextSegmentMutation> mutations)
    {
        if (mutations.Count == 0)
            return;

        bool requestMerge = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Remove(committedTransactionId);
            foreach (IGrouping<string, FullTextSegmentMutation> group in mutations.GroupBy(
                         static mutation => mutation.Definition.Name,
                         StringComparer.Ordinal))
            {
                FullTextSegmentMutation[] entries = group.ToArray();
                FullTextIndexDefinition definition = entries[0].Definition;
                IndexState state = GetOrCreateState(definition);
                if (!DefinitionEquivalent(state.Current.Definition, definition))
                {
                    state.Dispose();
                    state = new IndexState(definition);
                    _indexes[definition.Name] = state;
                }

                var delta = new ImmutableFullTextSegment(
                    definition,
                    entries,
                    ResolveTokenizer(definition));
                ManifestVersion previous = state.Current;
                state.History.Add(previous with { Xmax = committedTransactionId });
                state.Current = new(
                    previous.Generation + 1,
                    committedTransactionId,
                    null,
                    definition,
                    previous.Segments.Add(delta),
                    previous.CoversPrimarySnapshot);

                FullTextSegmentPolicy policy = definition.SegmentPolicy ?? new();
                int deltaEntries = state.Current.Segments.Sum(static segment => segment.EntryCount);
                int tombstones = state.Current.Segments.Sum(static segment => segment.TombstoneCount);
                requestMerge |= deltaEntries >= policy.MaximumDeltaEntries
                    || state.Current.Segments.Length > policy.MaximumSegments
                    || deltaEntries > 0
                    && (double)tombstones / deltaEntries > policy.MaximumTombstoneRatio;
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
            IReadOnlyList<FullTextSegmentMutation> primary = ScanPrimary(
                transaction,
                definition,
                _labels,
                _edgeTypes,
                _nexusTypes,
                _propertyKeys);
            var rebuilt = new ImmutableFullTextSegment(
                definition,
                primary,
                ResolveTokenizer(definition));
            manifest = new(
                0,
                TransactionId.Bootstrap.Value,
                null,
                definition,
                [rebuilt],
                CoversPrimarySnapshot: true);
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
                        definition,
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
        snapshot = Open(transaction, FromCatalog(catalog));
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
        IReadOnlyList<FullTextSegmentMutation> primary)
        => new(
            source.IndexName,
            source.ManifestGeneration,
            source.Definition,
            new ImmutableFullTextSegment(
                source.Definition,
                primary,
                ResolveTokenizer(source.Definition)));

    internal bool TryPublishMerge(
        long committedTransactionId,
        FullTextSegmentBuildArtifact artifact,
        IndexDefinition? currentDefinition)
    {
        lock (_gate)
        {
            if (!_indexes.TryGetValue(artifact.IndexName, out IndexState? state)
                || state.Current.Generation != artifact.SourceManifestGeneration
                || !DefinitionEquivalent(state.Current.Definition, artifact.Definition)
                || currentDefinition is not FullTextIndexDefinition current
                || !DefinitionEquivalent(current, artifact.Definition))
                return false;

            ManifestVersion previous = state.Current;
            state.History.Add(previous with { Xmax = committedTransactionId });
            state.Current = new(
                previous.Generation + 1,
                committedTransactionId,
                null,
                artifact.Definition,
                [artifact.Segment],
                CoversPrimarySnapshot: true);
            return true;
        }
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

    private IndexState GetOrCreateState(FullTextIndexDefinition definition)
    {
        if (_indexes.TryGetValue(definition.Name, out IndexState? state))
            return state;
        state = new(definition);
        _indexes.Add(definition.Name, state);
        return state;
    }

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
            && left.K1 == right.K1
            && left.B == right.B
            && left.SegmentPolicy == right.SegmentPolicy;

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
            CoversPrimarySnapshot: false);

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
        bool CoversPrimarySnapshot);
}

internal sealed class ImmutableFullTextSegment : IDisposable
{
    private readonly FullTextSegmentMutation[] _entries;
    private readonly Dictionary<string, List<(long Owner, int Tf)>> _postings;
    private readonly Dictionary<long, int> _norms;
    private readonly HashSet<long> _tombstones;

    internal ImmutableFullTextSegment(
        FullTextIndexDefinition definition,
        IReadOnlyList<FullTextSegmentMutation> entries,
        ITokenizer tokenizer)
    {
        _entries = entries.ToArray();
        _postings = new(StringComparer.Ordinal);
        _norms = [];
        _tombstones = [];
        foreach (FullTextSegmentMutation entry in _entries)
        {
            long owner = EntityRef.Pack(
                entry.Owner.Kind,
                entry.Owner.Sequence,
                entry.Owner.Generation);
            if (entry.Text is null)
            {
                _tombstones.Add(owner);
                continue;
            }

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

    internal int EntryCount => _entries.Length;
    internal int TombstoneCount => _tombstones.Count;
    internal IReadOnlyList<FullTextSegmentMutation> Entries => _entries;
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
        var latest = new Dictionary<long, FullTextSegmentMutation>();
        for (int segmentIndex = segments.Length - 1; segmentIndex >= 0; segmentIndex--)
        {
            IReadOnlyList<FullTextSegmentMutation> entries = segments[segmentIndex].Entries;
            for (int entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
            {
                FullTextSegmentMutation entry = entries[entryIndex];
                long owner = EntityRef.Pack(
                    entry.Owner.Kind,
                    entry.Owner.Sequence,
                    entry.Owner.Generation);
                latest.TryAdd(owner, entry);
            }
        }

        foreach ((long owner, FullTextSegmentMutation mutation) in latest)
        {
            if (mutation.Text is null)
                continue;
            _propertyVersions[owner] = mutation.PropertyVersion;
            var sink = new TfSink();
            tokenizer.Tokenize(mutation.Text, sink);
            foreach ((string term, int tf) in sink.Frequencies)
            {
                if (!_postings.TryGetValue(term, out List<(long, int)>? list))
                    _postings.Add(term, list = []);
                list.Add((owner, Math.Min(tf, ushort.MaxValue)));
            }
            _norms[owner] = tokenizer is INormTokenCounter counter
                ? counter.CountNormTokens(mutation.Text)
                : sink.Total;
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

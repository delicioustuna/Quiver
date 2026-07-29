using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Logical;
using Quiver.Storage.Records;

namespace Quiver;

/// <summary>semantic merge時に既存target propertyとsource propertyが競合した場合の処理。</summary>
public enum GraphJsonPropertyConflictPolicy
{
    /// <summary>値、物理型、またはcardinalityが異なる場合はimportを失敗させる。</summary>
    Error,

    /// <summary>既存targetの値を保持し、sourceの値を適用しない。</summary>
    KeepTarget,

    /// <summary>既存targetの値をsourceの値で置き換える。</summary>
    OverwriteTarget,
}

/// <summary>Vertexを既存targetへ照合するlabelとSingle property key。</summary>
/// <param name="Label">照合対象のVertex label。</param>
/// <param name="PropertyKey">identityとして完全一致させるSingle property key。</param>
public readonly record struct GraphJsonVertexIdentityRule(
    string Label,
    string PropertyKey);

/// <summary>
/// graph JSONを既存target graphへ意味的にmergeする明示オプション。
/// 決定順のproperty適用まで全source property payloadを保持するため作業メモリはpayload総量に比例する。
/// target候補はoperation開始時に一度走査して作る一時indexへ保持する。
/// </summary>
public sealed class GraphJsonSemanticMergeOptions
{
    /// <summary>
    /// labelごとのVertex identity rule。一つのlabelへ複数ruleは指定できない。
    /// ruleがないlabelのVertexはappendされる。
    /// </summary>
    public IReadOnlyList<GraphJsonVertexIdentityRule> VertexIdentityRules { get; set; } = [];

    /// <summary>identity以外のpropertyが既存targetと競合した場合の処理。</summary>
    public GraphJsonPropertyConflictPolicy PropertyConflictPolicy { get; set; }
        = GraphJsonPropertyConflictPolicy.Error;
}

internal sealed partial class GraphJsonImportOperation
{
    private Dictionary<string, GraphJsonVertexIdentityRule>? _semanticIdentityRules;
    private Dictionary<(string Label, string Key), PropertyValueType>? _semanticIdentityTypes;
    private Dictionary<SemanticVertexIdentityKey, List<VertexId>>? _semanticVertexIndex;
    private Dictionary<(string Label, string Key), Dictionary<PropertyValueType, VertexId>>?
        _semanticTargetIdentityTypes;
    private Dictionary<SemanticEdgeShape, List<EdgeId>>? _semanticEdgeIndex;
    private Dictionary<SemanticNexusShape, List<NexusId>>? _semanticNexusIndex;
    private List<DeferredSemanticProperties>? _deferredSemanticProperties;
    private long _semanticMatches;

    private bool SemanticMergeEnabled => _options.SemanticMerge is not null;

    private string? IdentityKey(string label)
    {
        EnsureSemanticOptions();
        return _semanticIdentityRules!.TryGetValue(label, out GraphJsonVertexIdentityRule rule)
            ? rule.PropertyKey
            : null;
    }

    private (VertexId Id, bool Created) ResolveSemanticVertex(
        GraphJsonSourceKey source,
        GraphJsonVertexDefinition vertex,
        GraphJsonStreamingReader reader)
    {
        EnsureSemanticOptions();
        EnsureSemanticIndexes(reader);
        if (!_semanticIdentityRules!.TryGetValue(
                vertex.Label,
                out GraphJsonVertexIdentityRule rule))
        {
            return (_transaction.CreateVertex(vertex.Label), true);
        }

        GraphJsonImportProperty[] identity = vertex.Properties
            .Where(property => StringComparer.Ordinal.Equals(property.Key, rule.PropertyKey))
            .ToArray();
        if (identity.Length != 1 || identity[0].Cardinality != PropertyCardinality.Single)
        {
            throw SemanticError(
                reader,
                $"Vertex {source.PackedId} のidentity property '{rule.PropertyKey}' はSingle値を一つ持つ必要があります。");
        }

        GraphJsonImportProperty sourceIdentity = identity[0];
        var typeKey = (rule.Label, rule.PropertyKey);
        if (_semanticIdentityTypes!.TryGetValue(typeKey, out PropertyValueType expectedType)
            && expectedType != sourceIdentity.Value.Type)
        {
            throw SemanticError(
                reader,
                $"identity property '{rule.PropertyKey}' の物理型が{expectedType}と{sourceIdentity.Value.Type}で競合しています。");
        }
        _semanticIdentityTypes[typeKey] = sourceIdentity.Value.Type;

        Dictionary<PropertyValueType, VertexId> targetTypes =
            _semanticTargetIdentityTypes![typeKey];
        foreach ((PropertyValueType type, VertexId candidate) in targetTypes)
        {
            if (type != sourceIdentity.Value.Type)
            {
                throw SemanticError(
                    reader,
                    $"target Vertex {candidate} のidentity property '{rule.PropertyKey}' の物理型が一致しません。");
            }
        }

        var identityKey = new SemanticVertexIdentityKey(
            rule.Label,
            rule.PropertyKey,
            SemanticValueKey.FromSource(sourceIdentity.Value));
        _semanticVertexIndex!.TryGetValue(identityKey, out List<VertexId>? candidates);
        if (candidates is { Count: > 1 })
        {
            throw SemanticError(
                reader,
                $"Vertex label '{rule.Label}' とidentity property '{rule.PropertyKey}' に一致するtarget候補が複数あります。");
        }
        if (candidates is { Count: 1 })
        {
            _semanticMatches++;
            return (candidates[0], false);
        }

        VertexId created = _transaction.CreateVertex(vertex.Label);
        ApplyProperties(EntityKind.Vertex, created.Value, [sourceIdentity]);
        AddCandidate(_semanticVertexIndex, identityKey, created);
        targetTypes.TryAdd(sourceIdentity.Value.Type, created);
        return (created, true);
    }

    private (EdgeId Id, bool Created) ResolveSemanticEdge(
        VertexId source,
        VertexId target,
        string type,
        GraphJsonStreamingReader reader)
    {
        EnsureSemanticIndexes(reader);
        var shape = new SemanticEdgeShape(type, source, target);
        _semanticEdgeIndex!.TryGetValue(shape, out List<EdgeId>? candidates);
        if (candidates is { Count: > 1 })
        {
            throw SemanticError(
                reader,
                $"Edge ({source})-[{type}]->({target}) に一致するtarget候補が複数あります。");
        }
        if (candidates is { Count: 1 })
        {
            _semanticMatches++;
            return (candidates[0], false);
        }
        EdgeId created = _transaction.CreateEdge(source, target, type);
        AddCandidate(_semanticEdgeIndex, shape, created);
        return (created, true);
    }

    private (NexusId Id, bool Created) ResolveSemanticNexus(
        string type,
        ReadOnlySpan<NexusMember> members,
        GraphJsonStreamingReader reader)
    {
        EnsureSemanticIndexes(reader);
        NexusMember[] normalized = members.ToArray();
        Array.Sort(normalized, SemanticNexusMemberComparer.Instance);
        var shape = new SemanticNexusShape(type, normalized);
        _semanticNexusIndex!.TryGetValue(shape, out List<NexusId>? candidates);
        if (candidates is { Count: > 1 })
        {
            throw SemanticError(
                reader,
                $"Nexus type '{type}' とmember集合に一致するtarget候補が複数あります。");
        }
        if (candidates is { Count: 1 })
        {
            _semanticMatches++;
            return (candidates[0], false);
        }
        NexusId created = _transaction.CreateNexus(type, members);
        AddCandidate(_semanticNexusIndex, shape, created);
        return (created, true);
    }

    private void DeferSemanticProperties(
        GraphJsonSourceKey source,
        long targetPackedId,
        IReadOnlyList<GraphJsonImportProperty> properties,
        string? identityKey)
    {
        (_deferredSemanticProperties ??= []).Add(new DeferredSemanticProperties(
            source,
            targetPackedId,
            properties,
            identityKey,
            _documentIndex));
    }

    private void CompleteSemanticMerge()
    {
        if (!SemanticMergeEnabled)
            return;
        EnsureSemanticOptions();
        if (_deferredSemanticProperties is null)
            return;

        _cancellationToken.ThrowIfCancellationRequested();
        _deferredSemanticProperties.Sort(DeferredSemanticPropertiesComparer.Instance);
        _cancellationToken.ThrowIfCancellationRequested();
        foreach (DeferredSemanticProperties deferred in _deferredSemanticProperties)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var groupsByKey = new Dictionary<string, List<GraphJsonImportProperty>>(
                StringComparer.Ordinal);
            var groups = new List<List<GraphJsonImportProperty>>();
            foreach (GraphJsonImportProperty property in deferred.Properties)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (StringComparer.Ordinal.Equals(property.Key, deferred.IdentityKey))
                    continue;
                if (!groupsByKey.TryGetValue(
                        property.Key,
                        out List<GraphJsonImportProperty>? group))
                {
                    group = [];
                    groupsByKey.Add(property.Key, group);
                    groups.Add(group);
                }
                group.Add(property);
            }

            foreach (List<GraphJsonImportProperty> group in groups)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ApplySemanticPropertyGroup(deferred, group);
            }
        }
    }

    private void ApplySemanticPropertyGroup(
        DeferredSemanticProperties deferred,
        IReadOnlyList<GraphJsonImportProperty> source)
    {
        string key = source[0].Key;
        List<CapturedSemanticProperty> target = CaptureTargetProperties(
            deferred.Source.Kind,
            deferred.TargetPackedId,
            key);
        if (target.Count == 0)
        {
            ApplyProperties(deferred.Source.Kind, deferred.TargetPackedId, source);
            return;
        }
        if (SamePropertySet(target, source))
            return;

        switch (_options.SemanticMerge!.PropertyConflictPolicy)
        {
            case GraphJsonPropertyConflictPolicy.Error:
                throw new GraphJsonImportException(
                    $"target {deferred.Source.Kind} のproperty '{key}' がsourceと競合しています。",
                    deferred.DocumentIndex);
            case GraphJsonPropertyConflictPolicy.KeepTarget:
                return;
            case GraphJsonPropertyConflictPolicy.OverwriteTarget:
                RemoveTargetPropertyValues(
                    deferred.Source.Kind,
                    deferred.TargetPackedId,
                    key,
                    target);
                ApplyProperties(deferred.Source.Kind, deferred.TargetPackedId, source);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(GraphJsonSemanticMergeOptions.PropertyConflictPolicy));
        }
    }

    private List<CapturedSemanticProperty> CaptureTargetProperties(
        EntityKind kind,
        long targetPackedId,
        string key)
    {
        if (!_transaction.Schema.TryGetPropertyKeyId(key, out PropertyKeyId keyId))
            return [];

        PropertyCursor cursor = kind switch
        {
            EntityKind.Vertex => _transaction.EnumerateProperties(new VertexId(targetPackedId)),
            EntityKind.Edge => _transaction.EnumerateProperties(new EdgeId(targetPackedId)),
            EntityKind.Nexus => _transaction.EnumerateProperties(new NexusId(targetPackedId)),
            _ => throw new InvalidOperationException($"未対応のentity kind {kind} です。"),
        };
        var result = new List<CapturedSemanticProperty>();
        try
        {
            while (cursor.MoveNext())
            {
                _cancellationToken.ThrowIfCancellationRequested();
                PropertyEntry property = cursor.Current;
                if (property.KeyId == keyId)
                {
                    result.Add(new CapturedSemanticProperty(
                        property.Cardinality,
                        LogicalPropertyValue.Capture(property.Value)));
                }
            }
        }
        finally
        {
            cursor.Dispose();
        }
        return result;
    }

    private void RemoveTargetPropertyValues(
        EntityKind kind,
        long targetPackedId,
        string key,
        IReadOnlyList<CapturedSemanticProperty> values)
    {
        if (values[0].Cardinality == PropertyCardinality.Single)
        {
            switch (kind)
            {
                case EntityKind.Vertex:
                    _transaction.RemoveProperty(new VertexId(targetPackedId), key);
                    return;
                case EntityKind.Edge:
                    _transaction.RemoveProperty(new EdgeId(targetPackedId), key);
                    return;
                case EntityKind.Nexus:
                    _transaction.RemoveProperty(new NexusId(targetPackedId), key);
                    return;
                default:
                    throw new InvalidOperationException($"未対応のentity kind {kind} です。");
            }
        }

        foreach (CapturedSemanticProperty captured in values)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            PropertyValue value = captured.Value.ToPropertyValue();
            switch (kind)
            {
                case EntityKind.Vertex:
                    _transaction.RemovePropertyValue(new VertexId(targetPackedId), key, in value);
                    break;
                case EntityKind.Edge:
                    _transaction.RemovePropertyValue(new EdgeId(targetPackedId), key, in value);
                    break;
                case EntityKind.Nexus:
                    _transaction.RemovePropertyValue(new NexusId(targetPackedId), key, in value);
                    break;
                default:
                    throw new InvalidOperationException($"未対応のentity kind {kind} です。");
            }
        }
    }

    private bool SamePropertySet(
        IReadOnlyList<CapturedSemanticProperty> target,
        IReadOnlyList<GraphJsonImportProperty> source)
    {
        if (target.Count != source.Count)
            return false;
        var used = new bool[target.Count];
        foreach (GraphJsonImportProperty sourceValue in source)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            bool found = false;
            for (int i = 0; i < target.Count; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (used[i]
                    || target[i].Cardinality != sourceValue.Cardinality
                    || !SameValue(target[i].Value, sourceValue.Value))
                {
                    continue;
                }
                used[i] = true;
                found = true;
                break;
            }
            if (!found)
                return false;
        }
        return true;
    }

    private static bool SameValue(
        LogicalPropertyValue target,
        GraphJsonImportValue source)
    {
        if (target.Type != source.Type)
            return false;
        PropertyValue targetValue = target.ToPropertyValue();
        PropertyValue sourceValue = source.AsPropertyValue();
        return PropertyValueEqualityHelper.AreEqual(in targetValue, in sourceValue);
    }

    private void EnsureSemanticOptions()
    {
        if (_semanticIdentityRules is not null)
            return;
        GraphJsonSemanticMergeOptions options = _options.SemanticMerge
            ?? throw new InvalidOperationException("semantic merge optionsがありません。");
        if (!Enum.IsDefined(options.PropertyConflictPolicy))
            throw new ArgumentOutOfRangeException(nameof(options.PropertyConflictPolicy));
        ArgumentNullException.ThrowIfNull(options.VertexIdentityRules);
        if (options.VertexIdentityRules.Count == 0)
            throw new ArgumentException(
                "semantic mergeには一つ以上のVertex identity ruleが必要です。",
                nameof(options.VertexIdentityRules));

        _semanticIdentityRules = new Dictionary<string, GraphJsonVertexIdentityRule>(
            StringComparer.Ordinal);
        _semanticIdentityTypes = [];
        foreach (GraphJsonVertexIdentityRule rule in options.VertexIdentityRules)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.Label);
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.PropertyKey);
            if (!_semanticIdentityRules.TryAdd(rule.Label, rule))
            {
                throw new ArgumentException(
                    $"Vertex label '{rule.Label}' にidentity ruleが複数指定されています。",
                    nameof(options.VertexIdentityRules));
            }
        }
    }

    private void EnsureSemanticIndexes(GraphJsonStreamingReader reader)
    {
        if (_semanticVertexIndex is not null)
            return;

        EnsureSemanticOptions();
        _semanticVertexIndex = [];
        _semanticTargetIdentityTypes = [];
        _semanticEdgeIndex = [];
        _semanticNexusIndex = [];

        foreach (GraphJsonVertexIdentityRule rule in _semanticIdentityRules!.Values)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _semanticTargetIdentityTypes.Add(
                (rule.Label, rule.PropertyKey),
                []);
        }

        _cancellationToken.ThrowIfCancellationRequested();
        foreach (VertexId vertex in _transaction.Query.Vertices().AsEnumerable())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string? label = _transaction.GetVertexLabel(vertex);
            if (label is null
                || !_semanticIdentityRules.TryGetValue(
                    label,
                    out GraphJsonVertexIdentityRule rule))
            {
                continue;
            }

            List<CapturedSemanticProperty> values = CaptureTargetProperties(
                EntityKind.Vertex,
                vertex.Value,
                rule.PropertyKey);
            if (values.Count == 0)
                continue;
            if (values.Count != 1
                || values[0].Cardinality != PropertyCardinality.Single)
            {
                throw SemanticError(
                    reader,
                    $"target Vertex {vertex} のidentity property '{rule.PropertyKey}' がSingle値ではありません。");
            }

            CapturedSemanticProperty identity = values[0];
            var typeKey = (rule.Label, rule.PropertyKey);
            _semanticTargetIdentityTypes[typeKey].TryAdd(identity.Value.Type, vertex);
            AddCandidate(
                _semanticVertexIndex,
                new SemanticVertexIdentityKey(
                    rule.Label,
                    rule.PropertyKey,
                    SemanticValueKey.FromTarget(identity.Value)),
                vertex);
        }

        _cancellationToken.ThrowIfCancellationRequested();
        foreach (EdgeId edgeId in _transaction.Query.Edges().AsEnumerable())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_transaction.TryGetEdge(edgeId, out EdgeInfo edge))
            {
                AddCandidate(
                    _semanticEdgeIndex,
                    new SemanticEdgeShape(edge.Type, edge.Source, edge.Target),
                    edge.Id);
            }
        }

        _cancellationToken.ThrowIfCancellationRequested();
        foreach (NexusId nexusId in _transaction.Query.Nexuses().AsEnumerable())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string? type = _transaction.GetNexusType(nexusId);
            if (type is null)
                continue;
            var members = new List<NexusMember>();
            NexusMemberEnumerator enumerator = _transaction.GetMembers(nexusId);
            try
            {
                while (enumerator.MoveNext())
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    members.Add(enumerator.Current);
                }
            }
            finally
            {
                enumerator.Dispose();
            }
            _cancellationToken.ThrowIfCancellationRequested();
            members.Sort(SemanticNexusMemberComparer.Instance);
            _cancellationToken.ThrowIfCancellationRequested();
            AddCandidate(
                _semanticNexusIndex,
                new SemanticNexusShape(type, members.ToArray()),
                nexusId);
        }
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private static void AddCandidate<TKey, TEntityId>(
        Dictionary<TKey, List<TEntityId>> index,
        TKey key,
        TEntityId id)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out List<TEntityId>? candidates))
        {
            candidates = [];
            index.Add(key, candidates);
        }
        candidates.Add(id);
    }

    private static GraphJsonImportException SemanticError(
        GraphJsonStreamingReader reader,
        string message)
        => new(message, reader.DocumentIndex, reader.Current.ByteOffset);

    private readonly record struct CapturedSemanticProperty(
        PropertyCardinality Cardinality,
        LogicalPropertyValue Value);

    private readonly record struct SemanticVertexIdentityKey(
        string Label,
        string PropertyKey,
        SemanticValueKey Value);

    private readonly record struct SemanticEdgeShape(
        string Type,
        VertexId Source,
        VertexId Target);

    private sealed class SemanticValueKey : IEquatable<SemanticValueKey>
    {
        private readonly int _hashCode;

        private SemanticValueKey(PropertyValueType type, byte[] canonicalBytes)
        {
            Type = type;
            CanonicalBytes = canonicalBytes;
            var hash = new HashCode();
            hash.Add(type);
            foreach (byte value in canonicalBytes)
                hash.Add(value);
            _hashCode = hash.ToHashCode();
        }

        private PropertyValueType Type { get; }
        private byte[] CanonicalBytes { get; }

        internal static SemanticValueKey FromSource(GraphJsonImportValue value)
            => new(value.Type, value.CanonicalBytes);

        internal static SemanticValueKey FromTarget(LogicalPropertyValue value)
        {
            byte[] canonical = value.Type switch
            {
                PropertyValueType.Bool => [value.Scalar == 0 ? (byte)0 : (byte)1],
                PropertyValueType.Int32 => WriteInt32((int)value.Scalar),
                PropertyValueType.Int64 => WriteInt64(value.Scalar),
                PropertyValueType.Double => WriteInt64(value.Scalar),
                PropertyValueType.String => value.Bytes ?? [],
                PropertyValueType.Bytes => value.Bytes ?? [],
                PropertyValueType.FloatArray => CanonicalizeFloatArray(value.Bytes ?? []),
                _ => [],
            };
            return new SemanticValueKey(value.Type, canonical);
        }

        public bool Equals(SemanticValueKey? other)
            => other is not null
               && Type == other.Type
               && CanonicalBytes.AsSpan().SequenceEqual(other.CanonicalBytes);

        public override bool Equals(object? obj)
            => obj is SemanticValueKey other && Equals(other);

        public override int GetHashCode() => _hashCode;

        private static byte[] WriteInt32(int value)
        {
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] WriteInt64(long value)
        {
            byte[] bytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] CanonicalizeFloatArray(byte[] bytes)
        {
            if (bytes.Length % sizeof(float) != 0)
                throw new InvalidOperationException("FloatArray payload長が不正です。");
            byte[] canonical = new byte[bytes.Length];
            for (int offset = 0; offset < bytes.Length; offset += sizeof(float))
            {
                int bits = BitConverter.ToInt32(bytes, offset);
                BinaryPrimitives.WriteInt32BigEndian(
                    canonical.AsSpan(offset, sizeof(int)),
                    bits);
            }
            return canonical;
        }
    }

    private sealed class SemanticNexusShape : IEquatable<SemanticNexusShape>
    {
        private readonly int _hashCode;

        internal SemanticNexusShape(string type, NexusMember[] members)
        {
            Type = type;
            Members = members;
            var hash = new HashCode();
            hash.Add(type, StringComparer.Ordinal);
            foreach (NexusMember member in members)
            {
                hash.Add(member.Role, StringComparer.Ordinal);
                hash.Add(member.VertexId);
            }
            _hashCode = hash.ToHashCode();
        }

        private string Type { get; }
        private NexusMember[] Members { get; }

        public bool Equals(SemanticNexusShape? other)
            => other is not null
               && StringComparer.Ordinal.Equals(Type, other.Type)
               && Members.AsSpan().SequenceEqual(other.Members);

        public override bool Equals(object? obj)
            => obj is SemanticNexusShape other && Equals(other);

        public override int GetHashCode() => _hashCode;
    }

    private sealed record DeferredSemanticProperties(
        GraphJsonSourceKey Source,
        long TargetPackedId,
        IReadOnlyList<GraphJsonImportProperty> Properties,
        string? IdentityKey,
        int DocumentIndex);

    private sealed class DeferredSemanticPropertiesComparer
        : IComparer<DeferredSemanticProperties>
    {
        internal static DeferredSemanticPropertiesComparer Instance { get; } = new();

        public int Compare(DeferredSemanticProperties? left, DeferredSemanticProperties? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int comparison = left.Source.DatabaseId.Value.CompareTo(right.Source.DatabaseId.Value);
            if (comparison != 0) return comparison;
            comparison = left.Source.Kind.CompareTo(right.Source.Kind);
            return comparison != 0
                ? comparison
                : left.Source.PackedId.CompareTo(right.Source.PackedId);
        }
    }

    private sealed class SemanticNexusMemberComparer : IComparer<NexusMember>
    {
        internal static SemanticNexusMemberComparer Instance { get; } = new();

        public int Compare(NexusMember left, NexusMember right)
        {
            int comparison = StringComparer.Ordinal.Compare(left.Role, right.Role);
            return comparison != 0
                ? comparison
                : left.VertexId.Value.CompareTo(right.VertexId.Value);
        }
    }
}

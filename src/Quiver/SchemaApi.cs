using Quiver.Core;
using Quiver.Index;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

internal interface INexusSchemaResolver
{
    bool TryGetRoleId(string name, out RoleId id);
}

internal readonly record struct ScalarIndexBuildEntry(object Key, long PropertyVersion);

internal sealed record ScalarIndexBuildArtifact(
    ScalarIndexDefinition Definition,
    long SourceCommittedHighWater,
    IReadOnlyList<ScalarIndexBuildEntry> Entries);

internal sealed class SchemaApi : ISchemaEditor, INexusSchemaResolver
{
    private readonly ITokenStore<LabelId> _labels;
    private readonly ITokenStore<EdgeTypeId> _edgeTypes;
    private readonly PropertyKeyTokenStore _propKeys;
    private readonly ITokenStore<NexusTypeId> _nexusTypes;
    private readonly ITokenStore<RoleId> _roles;
    private readonly IIndexManager _indexManager;
    private readonly IVectorDefinitionCatalog? _vectorDefinitions;
    private readonly Func<IDisposable>? _acquireMutationLease;
    private readonly Func<TransactionId, IDisposable>? _acquireOwnedMutationLease;
    private SnapshotSchemaCatalog _committedSnapshot;

    internal SchemaApi(
        ITokenStore<LabelId> labels,
        ITokenStore<EdgeTypeId> edgeTypes,
        PropertyKeyTokenStore propKeys,
        IIndexManager indexManager,
        ITokenStore<NexusTypeId> nexusTypes,
        ITokenStore<RoleId> roles,
        IVectorDefinitionCatalog? vectorDefinitions = null,
        Func<IDisposable>? acquireMutationLease = null,
        Func<TransactionId, IDisposable>? acquireOwnedMutationLease = null)
    {
        _labels = labels;
        _edgeTypes = edgeTypes;
        _propKeys = propKeys;
        _indexManager = indexManager;
        _nexusTypes = nexusTypes;
        _roles = roles;
        _vectorDefinitions = vectorDefinitions;
        _acquireMutationLease = acquireMutationLease;
        _acquireOwnedMutationLease = acquireOwnedMutationLease;
        _committedSnapshot = new SnapshotSchemaCatalog(this);
    }

    /// <summary>テスト用: 全文索引の postings/norms を直接検査するための内部アクセサ。</summary>
    internal IIndexManager IndexManager => _indexManager;

    public LabelId GetOrCreateLabel(string name)
    {
        if (_labels.TryGet(name, out LabelId existing)) return existing;
        return WithMutationLease(() => _labels.GetOrCreate(name));
    }

    private LabelId GetOrCreateLabel(string name, TransactionId owner)
    {
        if (_labels.TryGet(name, out LabelId existing)) return existing;
        return WithMutationLease(owner, () => _labels.GetOrCreate(name));
    }

    public EdgeTypeId GetOrCreateEdgeType(string name)
    {
        if (_edgeTypes.TryGet(name, out EdgeTypeId existing)) return existing;
        return WithMutationLease(() => _edgeTypes.GetOrCreate(name));
    }

    private EdgeTypeId GetOrCreateEdgeType(string name, TransactionId owner)
    {
        if (_edgeTypes.TryGet(name, out EdgeTypeId existing)) return existing;
        return WithMutationLease(owner, () => _edgeTypes.GetOrCreate(name));
    }

    public PropertyKeyId GetOrCreatePropertyKey(string name)
    {
        if (_propKeys.TryGet(name, out PropertyKeyId existing)) return existing;
        return WithMutationLease(() => _propKeys.GetOrCreate(name));
    }

    private PropertyKeyId GetOrCreatePropertyKey(string name, TransactionId owner)
    {
        if (_propKeys.TryGet(name, out PropertyKeyId existing)) return existing;
        return WithMutationLease(owner, () => _propKeys.GetOrCreate(name));
    }

    public PropertyKeyId GetOrCreatePropertyKey(string name, PropertyCardinality cardinality)
    {
        if (_propKeys.TryGet(name, out _))
            return _propKeys.GetOrCreate(name, cardinality);
        return WithMutationLease(() => _propKeys.GetOrCreate(name, cardinality));
    }

    private PropertyKeyId GetOrCreatePropertyKey(
        string name,
        PropertyCardinality cardinality,
        TransactionId owner)
    {
        if (_propKeys.TryGet(name, out _))
            return _propKeys.GetOrCreate(name, cardinality);
        return WithMutationLease(owner, () => _propKeys.GetOrCreate(name, cardinality));
    }
    public PropertyCardinality GetPropertyKeyCardinality(PropertyKeyId id) => _propKeys.GetCardinality(id);

    public string? GetLabelName(LabelId id) => id.IsValid ? _labels.GetName(id) : null;

    // 冪等な rename 判定用。auto-create を回避するため TokenStore.TryGet を直叩き。
    public bool TryGetLabelId(string name, out LabelId id) => _labels.TryGet(name, out id);
    public bool TryGetPropertyKeyId(string name, out PropertyKeyId id) => _propKeys.TryGet(name, out id);
    public bool TryGetEdgeTypeId(string name, out EdgeTypeId id) => _edgeTypes.TryGet(name, out id);

    public bool IndexExists(string indexName)
        => _indexManager.ListIndexes().Contains(indexName)
            || _vectorDefinitions?.TryGet(indexName, out _) == true;

    public void CreateIndex(IndexDefinition definition)
        => _ = CreateIndex(definition, owner: null);

    private bool CreateIndex(
        IndexDefinition definition,
        TransactionId? owner)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition is VectorIndexDefinition vector)
            return CreateVectorIndex(vector, owner);
        if (definition is not ScalarIndexDefinition scalar)
            throw new NotSupportedException(
                $"index definition '{definition.GetType().Name}' はサポートされていません。");

        ScalarIndexMetadata existing = _indexManager.ListIndexDefinitions()
            .FirstOrDefault(x => x.Definition.Name == scalar.Name);
        if (existing.Definition is { } existingDefinition)
        {
            if (existingDefinition == scalar)
                return false;
            throw new ConstraintException(
                $"Index definition '{scalar.Name}' already exists with a different target or kind.");
        }

        string indexName = scalar.Name;
        string propertyKey = scalar.Target.PropertyKey;
        string label = scalar.Target.Scope ?? string.Empty;
        IndexKind kind = scalar.Kind;
        using var lease = owner is { } transactionId
            ? AcquireMutationLease(transactionId)
            : AcquireMutationLease();
        switch (kind)
        {
            case IndexKind.Int32Equality:
                _indexManager.CreateInt32Index(indexName);
                break;
            case IndexKind.Int64Equality:
                _indexManager.CreateInt64Index(indexName);
                break;
            case IndexKind.DoubleEquality:
                _indexManager.CreateDoubleIndex(indexName);
                break;
            case IndexKind.StringEquality:
            case IndexKind.StringRange:
                _indexManager.CreateStringIndex(indexName);
                break;
        }
        _indexManager.RegisterIndexDefinition(
            scalar,
            owner is null ? IndexLifecycleState.Ready : IndexLifecycleState.Building);
        return true;
    }

    private bool CreateVectorIndex(
        VectorIndexDefinition definition,
        TransactionId? owner)
    {
        if (_vectorDefinitions is null)
            throw new NotSupportedException("このbackendはvector indexをサポートしていません。");
        if (_vectorDefinitions.TryGet(
                definition.Name,
                out VectorIndexDescriptor? existing))
        {
            VectorIndexDefinition current = ToDefinition(existing);
            if (current == definition)
                return false;
            throw new ConstraintException(
                $"Index definition '{definition.Name}' already exists with a different target or options.");
        }

        PropertyKeyId propertyKey = owner is { } transactionId
            ? GetOrCreatePropertyKey(definition.Target.PropertyKey, transactionId)
            : GetOrCreatePropertyKey(definition.Target.PropertyKey);
        var descriptor = new VectorIndexDescriptor(
            definition.Name,
            ToEntityKind(definition.Target.OwnerKind),
            propertyKey,
            definition.Target.Scope,
            definition.Dimensions,
            definition.Metric,
            definition.ElementType,
            definition.HnswM,
            definition.HnswMMax0,
            definition.HnswMaxLayers,
            definition.HnswEfConstruction,
            definition.SegmentPolicy);
        using var lease = owner is { } ownedTransaction
            ? AcquireMutationLease(ownedTransaction)
            : AcquireMutationLease();
        _vectorDefinitions.Create(descriptor);
        return true;
    }

    private void BuildScalarIndex(ITransaction transaction, ScalarIndexDefinition definition)
    {
        switch (definition.Target.OwnerKind)
        {
            case PropertyOwnerKind.Vertex:
                foreach (var vertexId in transaction.Vertices.Scan())
                {
                    var vertex = transaction.Vertices.Read(vertexId);
                    if (!vertex.InUse
                        || definition.Target.Scope is { } label
                            && _labels.GetName(vertex.Label) != label)
                        continue;
                    IndexProperties(
                        transaction,
                        definition,
                        EntityRef.From(vertexId),
                        transaction.Vertices.EnumerateProperties(vertexId, transaction.Properties));
                }
                break;

            case PropertyOwnerKind.Edge:
                foreach (var edgeId in transaction.Edges.Scan())
                {
                    var edge = transaction.Edges.Read(edgeId);
                    if (!edge.InUse
                        || definition.Target.Scope is { } type
                            && _edgeTypes.GetName(edge.Type) != type)
                        continue;
                    IndexProperties(
                        transaction,
                        definition,
                        EntityRef.From(edgeId),
                        transaction.Edges.EnumerateProperties(edgeId, transaction.Properties));
                }
                break;

            case PropertyOwnerKind.Nexus:
                foreach (var nexusId in transaction.Nexuses.Scan())
                {
                    using var nexus = transaction.Nexuses.Read(nexusId);
                    if (!nexus.InUse
                        || definition.Target.Scope is { } type
                            && _nexusTypes.GetName(nexus.Type) != type)
                        continue;
                    IndexProperties(
                        transaction,
                        definition,
                        EntityRef.From(nexusId),
                        transaction.Nexuses.EnumerateProperties(nexusId, transaction.Properties));
                }
                break;
        }
    }

    private void IndexProperties(
        ITransaction transaction,
        ScalarIndexDefinition definition,
        EntityRef owner,
        PropertyCursor properties)
    {
        while (properties.MoveNext())
        {
            PropertyEntry current = properties.Current;
            if (_propKeys.GetName(current.KeyId) != definition.Target.PropertyKey)
                continue;
            PropertyValue value = current.Value;
            InsertScalarValue(
                transaction.Indexes,
                definition,
                in value,
                properties.CurrentVersion.Value);
        }
    }

    private static void InsertScalarValue(
        IIndexManager indexes,
        ScalarIndexDefinition definition,
        in PropertyValue value,
        long propertyVersion)
    {
        switch (definition.Kind)
        {
            case IndexKind.Int32Equality when value.Type == PropertyValueType.Int32:
                indexes.CreateInt32Index(definition.Name).Insert(value.Int32Value, propertyVersion);
                break;
            case IndexKind.Int64Equality when value.Type is
                PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64:
                indexes.CreateInt64Index(definition.Name).Insert(value.Int64Value, propertyVersion);
                break;
            case IndexKind.DoubleEquality when value.Type == PropertyValueType.Double:
                indexes.CreateDoubleIndex(definition.Name).Insert(value.DoubleValue, propertyVersion);
                break;
            case IndexKind.StringEquality or IndexKind.StringRange
                when value.Type == PropertyValueType.String:
                indexes.CreateStringIndex(definition.Name).Insert(
                    System.Text.Encoding.UTF8.GetString(value.Utf8StringValue),
                    propertyVersion);
                break;
        }
    }

    public void DropIndex(string indexName)
        => DropIndex(indexName, owner: null);

    private void DropIndex(string indexName, TransactionId? owner)
    {
        using var lease = owner is { } transactionId
            ? AcquireMutationLease(transactionId)
            : AcquireMutationLease();
        if (_vectorDefinitions?.TryGet(indexName, out _) == true)
        {
            _vectorDefinitions.Drop(indexName);
            return;
        }
        _indexManager.DropIndex(indexName);
    }

    public IReadOnlyList<IndexInfo> ListIndexes()
    {
        var result = new List<IndexInfo>();
        foreach (var metadata in _indexManager.ListIndexDefinitions())
        {
            result.Add(new IndexInfo(
                metadata.Definition,
                metadata.State,
                0));
        }
        if (_vectorDefinitions is not null)
        {
            foreach (VectorIndexDescriptor descriptor in _vectorDefinitions.List())
            {
                result.Add(new IndexInfo(
                    ToDefinition(descriptor),
                    IndexLifecycleState.Ready,
                    0));
            }
        }
        return result;
    }

    public bool TryGetIndex(string indexName, out IndexInfo info)
    {
        foreach (IndexInfo candidate in ListIndexes())
        {
            if (candidate.Name != indexName)
                continue;
            info = candidate;
            return true;
        }
        info = default!;
        return false;
    }

    private VectorIndexDefinition ToDefinition(VectorIndexDescriptor descriptor)
    {
        string propertyKey = _propKeys.GetName(descriptor.TargetPropertyKeyId);
        return new VectorIndexDefinition(
            descriptor.Name,
            new PropertyTarget(
                ToOwnerKind(descriptor.OwnerKind),
                propertyKey,
                descriptor.TargetScope),
            descriptor.Dimensions,
            descriptor.Metric,
            descriptor.ElementType,
            descriptor.HnswM,
            descriptor.HnswMMax0,
            descriptor.HnswMaxLayers,
            descriptor.HnswEfConstruction,
            descriptor.SegmentPolicy);
    }

    private static EntityKind ToEntityKind(PropertyOwnerKind ownerKind)
        => ownerKind switch
        {
            PropertyOwnerKind.Vertex => EntityKind.Vertex,
            PropertyOwnerKind.Edge => EntityKind.Edge,
            PropertyOwnerKind.Nexus => EntityKind.Nexus,
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        };

    private static PropertyOwnerKind ToOwnerKind(EntityKind kind)
        => kind switch
        {
            EntityKind.Vertex => PropertyOwnerKind.Vertex,
            EntityKind.Edge => PropertyOwnerKind.Edge,
            EntityKind.Nexus => PropertyOwnerKind.Nexus,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public void CreateFullTextIndex(string indexName, string label, string propertyKey, FullTextIndexOptions? options = null)
        => CreateFullTextIndex(indexName, label, propertyKey, options, owner: null);

    private void CreateFullTextIndex(
        string indexName,
        string label,
        string propertyKey,
        FullTextIndexOptions? options,
        TransactionId? owner)
    {
        using var lease = owner is { } transactionId
            ? AcquireMutationLease(transactionId)
            : AcquireMutationLease();
        options ??= new FullTextIndexOptions();
        var tokenizerId = options.TokenizerId;
        if (options.Filters.Count > 0)
        {
            var baseTokenizer = _indexManager.ResolveTokenizer(tokenizerId);
            var filtered = new Text.FilteredTokenizer(baseTokenizer, [.. options.Filters]);
            tokenizerId = filtered.TokenizerId;
            _indexManager.RegisterTokenizer(filtered);
        }
        _indexManager.CreateFullTextIndex(indexName, label, propertyKey, tokenizerId);
    }

    public IReadOnlyList<FullTextIndexInfo> ListFullTextIndexes()
    {
        var result = new List<FullTextIndexInfo>();
        foreach (var (name, label, propKey, tokenizerId) in _indexManager.ListFullTextIndexes())
            result.Add(new FullTextIndexInfo(name, label, propKey, tokenizerId));
        return result;
    }

    public bool RenameLabel(string oldName, string newName)
        => WithMutationLease(() => RenameLabelCore(oldName, newName));
    private bool RenameLabel(string oldName, string newName, TransactionId owner)
        => WithMutationLease(owner, () => RenameLabelCore(oldName, newName));
    public bool RenamePropertyKey(string oldName, string newName)
        => WithMutationLease(() => RenamePropertyKeyCore(oldName, newName));
    private bool RenamePropertyKey(string oldName, string newName, TransactionId owner)
        => WithMutationLease(owner, () => RenamePropertyKeyCore(oldName, newName));
    public bool RenameEdgeType(string oldName, string newName)
        => WithMutationLease(() => RenameEdgeTypeCore(oldName, newName));
    private bool RenameEdgeType(string oldName, string newName, TransactionId owner)
        => WithMutationLease(owner, () => RenameEdgeTypeCore(oldName, newName));

    private bool RenameLabelCore(string oldName, string newName)
    {
        if (!_labels.Rename(oldName, newName))
            return false;
        _indexManager.RenameTargetScope(
            PropertyOwnerKind.Vertex,
            oldName,
            newName);
        return true;
    }

    private bool RenamePropertyKeyCore(string oldName, string newName)
    {
        if (!_propKeys.Rename(oldName, newName))
            return false;
        _indexManager.RenamePropertyTarget(oldName, newName);
        return true;
    }

    private bool RenameEdgeTypeCore(string oldName, string newName)
    {
        if (!_edgeTypes.Rename(oldName, newName))
            return false;
        _indexManager.RenameTargetScope(
            PropertyOwnerKind.Edge,
            oldName,
            newName);
        return true;
    }

    public bool RenameIndex(string oldName, string newName)
        => RenameIndex(oldName, newName, owner: null);

    private bool RenameIndex(string oldName, string newName, TransactionId? owner)
    {
        using var lease = owner is { } transactionId
            ? AcquireMutationLease(transactionId)
            : AcquireMutationLease();
        return _indexManager.RenameIndex(oldName, newName);
    }

    public IReadOnlyList<string> ListLabels()
        => _labels.All().Select(_labels.GetName).ToList();

    public IReadOnlyList<string> ListEdgeTypes()
        => _edgeTypes.All().Select(_edgeTypes.GetName).ToList();

    public IReadOnlyList<string> ListPropertyKeys()
        => _propKeys.All().Select(_propKeys.GetName).ToList();

    public NexusTypeId GetOrCreateNexusType(string name)
    {
        if (_nexusTypes.TryGet(name, out NexusTypeId existing)) return existing;
        return WithMutationLease(() => _nexusTypes.GetOrCreate(name));
    }
    private NexusTypeId GetOrCreateNexusType(string name, TransactionId owner)
    {
        if (_nexusTypes.TryGet(name, out NexusTypeId existing)) return existing;
        return WithMutationLease(owner, () => _nexusTypes.GetOrCreate(name));
    }
    public string? GetNexusTypeName(NexusTypeId id) => id.IsValid ? _nexusTypes.GetName(id) : null;
    public bool TryGetNexusTypeId(string name, out NexusTypeId id) => _nexusTypes.TryGet(name, out id);

    public IReadOnlyList<string> ListNexusTypes()
        => _nexusTypes.All().Select(_nexusTypes.GetName).ToList();

    public IReadOnlyList<string> ListRoles()
        => _roles.All().Select(_roles.GetName).ToList();

    public bool TryGetRoleId(string name, out RoleId id) => _roles.TryGet(name, out id);

    private IDisposable AcquireMutationLease()
        => _acquireMutationLease?.Invoke() ?? NoopDisposable.Instance;

    private IDisposable AcquireMutationLease(TransactionId owner)
        => _acquireOwnedMutationLease?.Invoke(owner) ?? AcquireMutationLease();

    private T WithMutationLease<T>(Func<T> action)
    {
        using var lease = AcquireMutationLease();
        return action();
    }

    private T WithMutationLease<T>(TransactionId owner, Func<T> action)
    {
        using var lease = AcquireMutationLease(owner);
        return action();
    }

    internal ISchemaCatalog CommittedCatalog
        => Volatile.Read(ref _committedSnapshot);

    internal ISchemaCatalog Bind(ITransaction transaction, bool readOnly)
    {
        if (readOnly)
            return CommittedCatalog;

        transaction.OnCommitted(PublishCommittedSnapshot);
        return new OwnedSchemaEditor(this, transaction);
    }

    private void PublishCommittedSnapshot()
        => Volatile.Write(
            ref _committedSnapshot,
            new SnapshotSchemaCatalog(this));

    private sealed class SnapshotSchemaCatalog : ISchemaCatalog, INexusSchemaResolver
    {
        private readonly Dictionary<int, string> _labelsById;
        private readonly Dictionary<string, LabelId> _labelIds;
        private readonly Dictionary<string, EdgeTypeId> _edgeTypeIds;
        private readonly Dictionary<string, PropertyKeyId> _propertyKeyIds;
        private readonly Dictionary<int, PropertyCardinality> _propertyCardinalities;
        private readonly Dictionary<int, string> _nexusTypesById;
        private readonly Dictionary<string, NexusTypeId> _nexusTypeIds;
        private readonly string[] _edgeTypes;
        private readonly string[] _propertyKeys;
        private readonly string[] _roles;
        private readonly Dictionary<string, RoleId> _roleIds;
        private readonly IndexInfo[] _indexes;
        private readonly FullTextIndexInfo[] _fullTextIndexes;

        internal SnapshotSchemaCatalog(SchemaApi schema)
        {
            string[] labels = [.. schema.ListLabels()];
            _labelIds = labels
                .Where(name => schema.TryGetLabelId(name, out _))
                .ToDictionary(
                    static name => name,
                    name =>
                    {
                        schema.TryGetLabelId(name, out LabelId id);
                        return id;
                    },
                    StringComparer.Ordinal);
            _labelsById = _labelIds.ToDictionary(
                static pair => pair.Value.Value,
                static pair => pair.Key);

            _edgeTypes = [.. schema.ListEdgeTypes()];
            _edgeTypeIds = _edgeTypes
                .Where(name => schema.TryGetEdgeTypeId(name, out _))
                .ToDictionary(
                    static name => name,
                    name =>
                    {
                        schema.TryGetEdgeTypeId(name, out EdgeTypeId id);
                        return id;
                    },
                    StringComparer.Ordinal);

            _propertyKeys = [.. schema.ListPropertyKeys()];
            _propertyKeyIds = _propertyKeys
                .Where(name => schema.TryGetPropertyKeyId(name, out _))
                .ToDictionary(
                    static name => name,
                    name =>
                    {
                        schema.TryGetPropertyKeyId(name, out PropertyKeyId id);
                        return id;
                    },
                    StringComparer.Ordinal);
            _propertyCardinalities = _propertyKeyIds.Values.ToDictionary(
                static id => id.Value,
                schema.GetPropertyKeyCardinality);

            string[] nexusTypes = [.. schema.ListNexusTypes()];
            _nexusTypeIds = nexusTypes
                .Where(name => schema.TryGetNexusTypeId(name, out _))
                .ToDictionary(
                    static name => name,
                    name =>
                    {
                        schema.TryGetNexusTypeId(name, out NexusTypeId id);
                        return id;
                    },
                    StringComparer.Ordinal);
            _nexusTypesById = _nexusTypeIds.ToDictionary(
                static pair => pair.Value.Value,
                static pair => pair.Key);

            _roles = [.. schema.ListRoles()];
            _roleIds = _roles
                .Where(name => schema.TryGetRoleId(name, out _))
                .ToDictionary(
                    static name => name,
                    name =>
                    {
                        schema.TryGetRoleId(name, out RoleId id);
                        return id;
                    },
                    StringComparer.Ordinal);
            _indexes = [.. schema.ListIndexes()];
            _fullTextIndexes = [.. schema.ListFullTextIndexes()];
        }

        public PropertyCardinality GetPropertyKeyCardinality(PropertyKeyId id)
            => _propertyCardinalities.GetValueOrDefault(
                id.Value,
                PropertyCardinality.Single);
        public string? GetLabelName(LabelId id)
            => _labelsById.GetValueOrDefault(id.Value);
        public bool TryGetLabelId(string name, out LabelId id)
            => _labelIds.TryGetValue(name, out id);
        public bool TryGetPropertyKeyId(string name, out PropertyKeyId id)
            => _propertyKeyIds.TryGetValue(name, out id);
        public bool TryGetEdgeTypeId(string name, out EdgeTypeId id)
            => _edgeTypeIds.TryGetValue(name, out id);
        public bool IndexExists(string indexName)
            => _indexes.Any(index => index.Name == indexName);
        public IReadOnlyList<IndexInfo> ListIndexes() => _indexes;
        public bool TryGetIndex(string indexName, out IndexInfo info)
        {
            foreach (IndexInfo candidate in _indexes)
            {
                if (candidate.Name != indexName)
                    continue;
                info = candidate;
                return true;
            }
            info = default!;
            return false;
        }
        public IReadOnlyList<FullTextIndexInfo> ListFullTextIndexes()
            => _fullTextIndexes;
        public IReadOnlyList<string> ListLabels() => [.. _labelIds.Keys];
        public IReadOnlyList<string> ListEdgeTypes() => _edgeTypes;
        public IReadOnlyList<string> ListPropertyKeys() => _propertyKeys;
        public string? GetNexusTypeName(NexusTypeId id)
            => _nexusTypesById.GetValueOrDefault(id.Value);
        public bool TryGetNexusTypeId(string name, out NexusTypeId id)
            => _nexusTypeIds.TryGetValue(name, out id);
        public IReadOnlyList<string> ListNexusTypes() => [.. _nexusTypeIds.Keys];
        public IReadOnlyList<string> ListRoles() => _roles;
        public bool TryGetRoleId(string name, out RoleId id)
            => _roleIds.TryGetValue(name, out id);
    }

    private sealed class OwnedSchemaEditor(SchemaApi schema, ITransaction transaction)
        : ISchemaEditor, INexusSchemaResolver
    {
        private TransactionId Owner => transaction.Id;

        public LabelId GetOrCreateLabel(string name) => schema.GetOrCreateLabel(name, Owner);
        public EdgeTypeId GetOrCreateEdgeType(string name) => schema.GetOrCreateEdgeType(name, Owner);
        public PropertyKeyId GetOrCreatePropertyKey(string name)
            => schema.GetOrCreatePropertyKey(name, Owner);
        public PropertyKeyId GetOrCreatePropertyKey(string name, PropertyCardinality cardinality)
            => schema.GetOrCreatePropertyKey(name, cardinality, Owner);
        public PropertyCardinality GetPropertyKeyCardinality(PropertyKeyId id)
            => schema.GetPropertyKeyCardinality(id);
        public string? GetLabelName(LabelId id) => schema.GetLabelName(id);
        public bool TryGetLabelId(string name, out LabelId id) => schema.TryGetLabelId(name, out id);
        public bool TryGetPropertyKeyId(string name, out PropertyKeyId id)
            => schema.TryGetPropertyKeyId(name, out id);
        public bool TryGetEdgeTypeId(string name, out EdgeTypeId id)
            => schema.TryGetEdgeTypeId(name, out id);
        public bool IndexExists(string indexName) => schema.IndexExists(indexName);
        public void CreateIndex(IndexDefinition definition)
        {
            if (schema.CreateIndex(definition, Owner)
                && definition is ScalarIndexDefinition scalar)
            {
                schema.BuildScalarIndex(transaction, scalar);
                transaction.Indexes.SetIndexState(scalar.Name, IndexLifecycleState.Ready);
            }
        }
        public void DropIndex(string indexName) => schema.DropIndex(indexName, Owner);
        public IReadOnlyList<IndexInfo> ListIndexes() => schema.ListIndexes();
        public bool TryGetIndex(string indexName, out IndexInfo info)
            => schema.TryGetIndex(indexName, out info);
        public void CreateFullTextIndex(
            string indexName,
            string label,
            string propertyKey,
            FullTextIndexOptions? options = null)
            => schema.CreateFullTextIndex(indexName, label, propertyKey, options, Owner);
        public IReadOnlyList<FullTextIndexInfo> ListFullTextIndexes()
            => schema.ListFullTextIndexes();
        public bool RenameLabel(string oldName, string newName)
            => schema.RenameLabel(oldName, newName, Owner);
        public bool RenamePropertyKey(string oldName, string newName)
            => schema.RenamePropertyKey(oldName, newName, Owner);
        public bool RenameEdgeType(string oldName, string newName)
            => schema.RenameEdgeType(oldName, newName, Owner);
        public bool RenameIndex(string oldName, string newName)
            => schema.RenameIndex(oldName, newName, Owner);
        public IReadOnlyList<string> ListLabels() => schema.ListLabels();
        public IReadOnlyList<string> ListEdgeTypes() => schema.ListEdgeTypes();
        public IReadOnlyList<string> ListPropertyKeys() => schema.ListPropertyKeys();
        public NexusTypeId GetOrCreateNexusType(string name)
            => schema.GetOrCreateNexusType(name, Owner);
        public string? GetNexusTypeName(NexusTypeId id) => schema.GetNexusTypeName(id);
        public bool TryGetNexusTypeId(string name, out NexusTypeId id)
            => schema.TryGetNexusTypeId(name, out id);
        public IReadOnlyList<string> ListNexusTypes() => schema.ListNexusTypes();
        public IReadOnlyList<string> ListRoles() => schema.ListRoles();
        public bool TryGetRoleId(string name, out RoleId id)
            => schema.TryGetRoleId(name, out id);

    }

    internal ScalarIndexBuildArtifact BuildScalarIndexArtifact(
        ITransaction transaction,
        ScalarIndexDefinition definition)
    {
        var entries = new List<ScalarIndexBuildEntry>();
        switch (definition.Target.OwnerKind)
        {
            case PropertyOwnerKind.Vertex:
                foreach (var vertexId in transaction.Vertices.Scan())
                {
                    var vertex = transaction.Vertices.Read(vertexId);
                    if (!vertex.InUse
                        || definition.Target.Scope is { } label
                            && _labels.GetName(vertex.Label) != label)
                        continue;
                    CollectScalarEntries(
                        transaction,
                        definition,
                        transaction.Vertices.EnumerateProperties(vertexId, transaction.Properties),
                        entries);
                }
                break;

            case PropertyOwnerKind.Edge:
                foreach (var edgeId in transaction.Edges.Scan())
                {
                    var edge = transaction.Edges.Read(edgeId);
                    if (!edge.InUse
                        || definition.Target.Scope is { } type
                            && _edgeTypes.GetName(edge.Type) != type)
                        continue;
                    CollectScalarEntries(
                        transaction,
                        definition,
                        transaction.Edges.EnumerateProperties(edgeId, transaction.Properties),
                        entries);
                }
                break;

            case PropertyOwnerKind.Nexus:
                foreach (var nexusId in transaction.Nexuses.Scan())
                {
                    using var nexus = transaction.Nexuses.Read(nexusId);
                    if (!nexus.InUse
                        || definition.Target.Scope is { } type
                            && _nexusTypes.GetName(nexus.Type) != type)
                        continue;
                    CollectScalarEntries(
                        transaction,
                        definition,
                        transaction.Nexuses.EnumerateProperties(nexusId, transaction.Properties),
                        entries);
                }
                break;
        }

        entries.Sort(static (left, right) =>
        {
            int keyOrder = Comparer<object>.Default.Compare(left.Key, right.Key);
            return keyOrder != 0
                ? keyOrder
                : left.PropertyVersion.CompareTo(right.PropertyVersion);
        });
        return new ScalarIndexBuildArtifact(
            definition,
            transaction.Snapshot.CommittedHighWater,
            entries);
    }

    internal bool TryPublishScalarIndexArtifact(
        ITransaction transaction,
        ScalarIndexBuildArtifact artifact)
    {
        if (transaction.Snapshot.CommittedHighWater != artifact.SourceCommittedHighWater)
            return false;

        ScalarIndexMetadata? current = transaction.Indexes
            .ListIndexDefinitions()
            .FirstOrDefault(x => x.Definition.Name == artifact.Definition.Name);
        if (current is not { } metadata
            || metadata.Definition != artifact.Definition
            || metadata.State == IndexLifecycleState.Ready)
            return false;

        transaction.Indexes.SetIndexState(
            artifact.Definition.Name,
            IndexLifecycleState.Building);
        transaction.Indexes.ResetIndexArtifact(artifact.Definition.Name);
        foreach (ScalarIndexBuildEntry entry in artifact.Entries)
            InsertScalarArtifactEntry(transaction.Indexes, artifact.Definition, entry);
        transaction.Indexes.SetIndexState(
            artifact.Definition.Name,
            IndexLifecycleState.Ready);
        return true;
    }

    private void CollectScalarEntries(
        ITransaction transaction,
        ScalarIndexDefinition definition,
        PropertyCursor properties,
        ICollection<ScalarIndexBuildEntry> entries)
    {
        while (properties.MoveNext())
        {
            PropertyEntry current = properties.Current;
            if (_propKeys.GetName(current.KeyId) != definition.Target.PropertyKey)
                continue;

            PropertyValue value = current.Value;
            object? key = definition.Kind switch
            {
                IndexKind.Int32Equality when value.Type == PropertyValueType.Int32
                    => value.Int32Value,
                IndexKind.Int64Equality when value.Type is
                    PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64
                    => value.Int64Value,
                IndexKind.DoubleEquality when value.Type == PropertyValueType.Double
                    => value.DoubleValue,
                IndexKind.StringEquality or IndexKind.StringRange
                    when value.Type == PropertyValueType.String
                    => System.Text.Encoding.UTF8.GetString(value.Utf8StringValue),
                _ => null,
            };
            if (key is not null)
                entries.Add(new ScalarIndexBuildEntry(
                    key,
                    properties.CurrentVersion.Value));
        }
    }

    private static void InsertScalarArtifactEntry(
        IIndexManager indexes,
        ScalarIndexDefinition definition,
        ScalarIndexBuildEntry entry)
    {
        switch (definition.Kind)
        {
            case IndexKind.Int32Equality:
                indexes.CreateInt32Index(definition.Name)
                    .Insert((int)entry.Key, entry.PropertyVersion);
                break;
            case IndexKind.Int64Equality:
                indexes.CreateInt64Index(definition.Name)
                    .Insert((long)entry.Key, entry.PropertyVersion);
                break;
            case IndexKind.DoubleEquality:
                indexes.CreateDoubleIndex(definition.Name)
                    .Insert((double)entry.Key, entry.PropertyVersion);
                break;
            case IndexKind.StringEquality:
            case IndexKind.StringRange:
                indexes.CreateStringIndex(definition.Name)
                    .Insert((string)entry.Key, entry.PropertyVersion);
                break;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

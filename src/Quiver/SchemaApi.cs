using Quiver.Core;
using Quiver.Index;
using Quiver.Storage.Records;

namespace Quiver;

internal interface INexusSchemaResolver
{
    bool TryGetRoleId(string name, out RoleId id);
}

internal sealed class SchemaApi : ISchemaApi, INexusSchemaResolver
{
    private readonly ITokenStore<LabelId> _labels;
    private readonly ITokenStore<EdgeTypeId> _edgeTypes;
    private readonly PropertyKeyTokenStore _propKeys;
    private readonly ITokenStore<NexusTypeId> _nexusTypes;
    private readonly ITokenStore<RoleId> _roles;
    private readonly IIndexManager _indexManager;
    private readonly Func<IDisposable>? _acquireMutationLease;
    private readonly Func<TransactionId, IDisposable>? _acquireOwnedMutationLease;

    internal SchemaApi(
        ITokenStore<LabelId> labels,
        ITokenStore<EdgeTypeId> edgeTypes,
        PropertyKeyTokenStore propKeys,
        IIndexManager indexManager,
        ITokenStore<NexusTypeId> nexusTypes,
        ITokenStore<RoleId> roles,
        Func<IDisposable>? acquireMutationLease = null,
        Func<TransactionId, IDisposable>? acquireOwnedMutationLease = null)
    {
        _labels = labels;
        _edgeTypes = edgeTypes;
        _propKeys = propKeys;
        _indexManager = indexManager;
        _nexusTypes = nexusTypes;
        _roles = roles;
        _acquireMutationLease = acquireMutationLease;
        _acquireOwnedMutationLease = acquireOwnedMutationLease;
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

    public bool IndexExists(string indexName) => _indexManager.ListIndexes().Contains(indexName);

    public void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind)
        => CreateIndex(indexName, label, propertyKey, kind, owner: null);

    private void CreateIndex(
        string indexName,
        string label,
        string propertyKey,
        IndexKind kind,
        TransactionId? owner)
    {
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
        // バインディングを登録し、MergeVertex が自動でこのインデックスを
        // 引けるようにする。kind は IndexInfo 側のメタデータ復元用に別途記録する。
        _indexManager.RegisterIndexBinding(indexName, label, propertyKey);
        _indexKinds[indexName] = kind;
    }

    public void DropIndex(string indexName)
        => DropIndex(indexName, owner: null);

    private void DropIndex(string indexName, TransactionId? owner)
    {
        using var lease = owner is { } transactionId
            ? AcquireMutationLease(transactionId)
            : AcquireMutationLease();
        _indexManager.DropIndex(indexName);
        _indexKinds.Remove(indexName);
    }

    public IReadOnlyList<IndexInfo> ListIndexes()
    {
        var bindings = _indexManager.ListIndexBindings()
            .ToDictionary(b => b.IndexName, b => (b.Label, b.PropertyKey), StringComparer.Ordinal);
        var result = new List<IndexInfo>();
        foreach (var n in _indexManager.ListIndexes())
        {
            var (label, propKey) = bindings.TryGetValue(n, out var b) ? b : ("", "");
            var kind = _indexKinds.TryGetValue(n, out var k) ? k : IndexKind.StringEquality;
            result.Add(new IndexInfo(n, label, propKey, kind, 0));
        }
        return result;
    }

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

        // IndexKind は IIndexManager の表現外なので SchemaApi 側で保持する。
    private readonly Dictionary<string, IndexKind> _indexKinds = new(StringComparer.Ordinal);

    public bool RenameLabel(string oldName, string newName)
        => WithMutationLease(() => _labels.Rename(oldName, newName));
    private bool RenameLabel(string oldName, string newName, TransactionId owner)
        => WithMutationLease(owner, () => _labels.Rename(oldName, newName));
    public bool RenamePropertyKey(string oldName, string newName)
        => WithMutationLease(() => _propKeys.Rename(oldName, newName));
    private bool RenamePropertyKey(string oldName, string newName, TransactionId owner)
        => WithMutationLease(owner, () => _propKeys.Rename(oldName, newName));
    public bool RenameEdgeType(string oldName, string newName)
        => WithMutationLease(() => _edgeTypes.Rename(oldName, newName));
    private bool RenameEdgeType(string oldName, string newName, TransactionId owner)
        => WithMutationLease(owner, () => _edgeTypes.Rename(oldName, newName));

    public bool RenameIndex(string oldName, string newName)
        => RenameIndex(oldName, newName, owner: null);

    private bool RenameIndex(string oldName, string newName, TransactionId? owner)
    {
        using var lease = owner is { } transactionId
            ? AcquireMutationLease(transactionId)
            : AcquireMutationLease();
        var ok = _indexManager.RenameIndex(oldName, newName);
        if (ok && _indexKinds.TryGetValue(oldName, out var kind))
        {
            _indexKinds.Remove(oldName);
            _indexKinds[newName] = kind;
        }
        return ok;
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

    internal ISchemaApi Bind(TransactionId owner) => new OwnedSchemaApi(this, owner);

    private sealed class OwnedSchemaApi(SchemaApi schema, TransactionId owner) : ISchemaApi
    {
        public LabelId GetOrCreateLabel(string name) => schema.GetOrCreateLabel(name, owner);
        public EdgeTypeId GetOrCreateEdgeType(string name) => schema.GetOrCreateEdgeType(name, owner);
        public PropertyKeyId GetOrCreatePropertyKey(string name)
            => schema.GetOrCreatePropertyKey(name, owner);
        public PropertyKeyId GetOrCreatePropertyKey(string name, PropertyCardinality cardinality)
            => schema.GetOrCreatePropertyKey(name, cardinality, owner);
        public PropertyCardinality GetPropertyKeyCardinality(PropertyKeyId id)
            => schema.GetPropertyKeyCardinality(id);
        public string? GetLabelName(LabelId id) => schema.GetLabelName(id);
        public bool TryGetLabelId(string name, out LabelId id) => schema.TryGetLabelId(name, out id);
        public bool TryGetPropertyKeyId(string name, out PropertyKeyId id)
            => schema.TryGetPropertyKeyId(name, out id);
        public bool TryGetEdgeTypeId(string name, out EdgeTypeId id)
            => schema.TryGetEdgeTypeId(name, out id);
        public bool IndexExists(string indexName) => schema.IndexExists(indexName);
        public void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind)
            => schema.CreateIndex(indexName, label, propertyKey, kind, owner);
        public void DropIndex(string indexName) => schema.DropIndex(indexName, owner);
        public IReadOnlyList<IndexInfo> ListIndexes() => schema.ListIndexes();
        public void CreateFullTextIndex(
            string indexName,
            string label,
            string propertyKey,
            FullTextIndexOptions? options = null)
            => schema.CreateFullTextIndex(indexName, label, propertyKey, options, owner);
        public IReadOnlyList<FullTextIndexInfo> ListFullTextIndexes()
            => schema.ListFullTextIndexes();
        public bool RenameLabel(string oldName, string newName)
            => schema.RenameLabel(oldName, newName, owner);
        public bool RenamePropertyKey(string oldName, string newName)
            => schema.RenamePropertyKey(oldName, newName, owner);
        public bool RenameEdgeType(string oldName, string newName)
            => schema.RenameEdgeType(oldName, newName, owner);
        public bool RenameIndex(string oldName, string newName)
            => schema.RenameIndex(oldName, newName, owner);
        public IReadOnlyList<string> ListLabels() => schema.ListLabels();
        public IReadOnlyList<string> ListEdgeTypes() => schema.ListEdgeTypes();
        public IReadOnlyList<string> ListPropertyKeys() => schema.ListPropertyKeys();
        public NexusTypeId GetOrCreateNexusType(string name)
            => schema.GetOrCreateNexusType(name, owner);
        public string? GetNexusTypeName(NexusTypeId id) => schema.GetNexusTypeName(id);
        public bool TryGetNexusTypeId(string name, out NexusTypeId id)
            => schema.TryGetNexusTypeId(name, out id);
        public IReadOnlyList<string> ListNexusTypes() => schema.ListNexusTypes();
        public IReadOnlyList<string> ListRoles() => schema.ListRoles();
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

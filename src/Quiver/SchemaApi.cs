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

    internal SchemaApi(
        ITokenStore<LabelId> labels,
        ITokenStore<EdgeTypeId> edgeTypes,
        PropertyKeyTokenStore propKeys,
        IIndexManager indexManager,
        ITokenStore<NexusTypeId> nexusTypes,
        ITokenStore<RoleId> roles)
    {
        _labels = labels;
        _edgeTypes = edgeTypes;
        _propKeys = propKeys;
        _indexManager = indexManager;
        _nexusTypes = nexusTypes;
        _roles = roles;
    }

    /// <summary>テスト用: 全文索引の postings/norms を直接検査するための内部アクセサ。</summary>
    internal IIndexManager IndexManager => _indexManager;

    public LabelId GetOrCreateLabel(string name) => _labels.GetOrCreate(name);
    public EdgeTypeId GetOrCreateEdgeType(string name) => _edgeTypes.GetOrCreate(name);
    public PropertyKeyId GetOrCreatePropertyKey(string name) => _propKeys.GetOrCreate(name);
    public PropertyKeyId GetOrCreatePropertyKey(string name, PropertyCardinality cardinality) => _propKeys.GetOrCreate(name, cardinality);
    public PropertyCardinality GetPropertyKeyCardinality(PropertyKeyId id) => _propKeys.GetCardinality(id);

    public string? GetLabelName(LabelId id) => id.IsValid ? _labels.GetName(id) : null;

    // 冪等な rename 判定用。auto-create を回避するため TokenStore.TryGet を直叩き。
    public bool TryGetLabelId(string name, out LabelId id) => _labels.TryGet(name, out id);
    public bool TryGetPropertyKeyId(string name, out PropertyKeyId id) => _propKeys.TryGet(name, out id);
    public bool TryGetEdgeTypeId(string name, out EdgeTypeId id) => _edgeTypes.TryGet(name, out id);

    public bool IndexExists(string indexName) => _indexManager.ListIndexes().Contains(indexName);

    public void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind)
    {
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
    {
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
    {
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

    public bool RenameLabel(string oldName, string newName) => _labels.Rename(oldName, newName);
    public bool RenamePropertyKey(string oldName, string newName) => _propKeys.Rename(oldName, newName);
    public bool RenameEdgeType(string oldName, string newName) => _edgeTypes.Rename(oldName, newName);

    public bool RenameIndex(string oldName, string newName)
    {
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

    public NexusTypeId GetOrCreateNexusType(string name) => _nexusTypes.GetOrCreate(name);
    public string? GetNexusTypeName(NexusTypeId id) => id.IsValid ? _nexusTypes.GetName(id) : null;
    public bool TryGetNexusTypeId(string name, out NexusTypeId id) => _nexusTypes.TryGet(name, out id);

    public IReadOnlyList<string> ListNexusTypes()
        => _nexusTypes.All().Select(_nexusTypes.GetName).ToList();

    public IReadOnlyList<string> ListRoles()
        => _roles.All().Select(_roles.GetName).ToList();

    public bool TryGetRoleId(string name, out RoleId id) => _roles.TryGet(name, out id);
}

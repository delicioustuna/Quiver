using Quiver;
using Quiver.Core;

namespace Quiver.Query.Physical.Tests.Support;

/// <summary>
/// プラン構造テスト用の最小 <see cref="ISchemaEditor"/> スタブ。
/// 実データベースなしでプランナーが名前を解決できるよう連番 ID を割り当てる。
/// </summary>
internal sealed class StubSchemaApi : ISchemaEditor, INexusSchemaResolver
{
    private int _nextLabel = 1;
    private int _nextEdgeType = 1;
    private int _nextPropKey = 1;
    private int _nextNexusType = 1;
    private int _nextRole = 1;

    private readonly Dictionary<string, LabelId> _labels = new();
    private readonly Dictionary<LabelId, string> _labelNames = new();
    private readonly Dictionary<string, EdgeTypeId> _edgeTypes = new();
    private readonly Dictionary<string, (PropertyKeyId Id, PropertyCardinality Card)> _propKeys = new();
    private readonly Dictionary<string, NexusTypeId> _nexusTypes = new();
    private readonly Dictionary<string, RoleId> _roles = new();

    public LabelId GetOrCreateLabel(string name)
    {
        if (!_labels.TryGetValue(name, out var id))
        {
            id = new LabelId(_nextLabel++);
            _labels[name] = id;
            _labelNames[id] = name;
        }
        return id;
    }

    public EdgeTypeId GetOrCreateEdgeType(string name)
    {
        if (!_edgeTypes.TryGetValue(name, out var id))
        {
            id = new EdgeTypeId(_nextEdgeType++);
            _edgeTypes[name] = id;
        }
        return id;
    }

    public PropertyKeyId GetOrCreatePropertyKey(string name)
        => GetOrCreatePropertyKey(name, PropertyCardinality.Single);

    public PropertyKeyId GetOrCreatePropertyKey(string name, PropertyCardinality cardinality)
    {
        if (_propKeys.TryGetValue(name, out var entry))
        {
            if (entry.Card != cardinality)
                throw new InvalidOperationException($"Cardinality mismatch for '{name}'.");
            return entry.Id;
        }
        var id = new PropertyKeyId(_nextPropKey++);
        _propKeys[name] = (id, cardinality);
        return id;
    }

    public PropertyCardinality GetPropertyKeyCardinality(PropertyKeyId id)
    {
        foreach (var kv in _propKeys)
            if (kv.Value.Id == id) return kv.Value.Card;
        return PropertyCardinality.Single;
    }

    public string? GetLabelName(LabelId id)
        => _labelNames.TryGetValue(id, out var n) ? n : null;

    public bool TryGetLabelId(string name, out LabelId id)
        => _labels.TryGetValue(name, out id);

    public bool TryGetPropertyKeyId(string name, out PropertyKeyId id)
    {
        if (_propKeys.TryGetValue(name, out var entry)) { id = entry.Id; return true; }
        id = default;
        return false;
    }

    public bool TryGetEdgeTypeId(string name, out EdgeTypeId id)
        => _edgeTypes.TryGetValue(name, out id);

    public bool IndexExists(string indexName) => false;
    public void CreateIndex(IndexDefinition definition) { }
    public void DropIndex(string indexName) { }
    public IReadOnlyList<IndexInfo> ListIndexes() => [];
    public void CreateFullTextIndex(string indexName, string label, string propertyKey, FullTextIndexOptions? options = null) { }
    public IReadOnlyList<FullTextIndexInfo> ListFullTextIndexes() => [];
    public bool RenameLabel(string oldName, string newName) => false;
    public bool RenamePropertyKey(string oldName, string newName) => false;
    public bool RenameEdgeType(string oldName, string newName) => false;
    public bool RenameIndex(string oldName, string newName) => false;
    public IReadOnlyList<string> ListLabels() => _labels.Keys.ToList();
    public IReadOnlyList<string> ListEdgeTypes() => _edgeTypes.Keys.ToList();
    public IReadOnlyList<string> ListPropertyKeys() => _propKeys.Keys.ToList();

    public NexusTypeId GetOrCreateNexusType(string name)
    {
        if (!_nexusTypes.TryGetValue(name, out var id))
        {
            id = new NexusTypeId(_nextNexusType++);
            _nexusTypes[name] = id;
        }
        return id;
    }

    public string? GetNexusTypeName(NexusTypeId id)
        => _nexusTypes.FirstOrDefault(kv => kv.Value == id).Key;

    public bool TryGetNexusTypeId(string name, out NexusTypeId id)
        => _nexusTypes.TryGetValue(name, out id);

    public IReadOnlyList<string> ListNexusTypes() => _nexusTypes.Keys.ToList();
    public IReadOnlyList<string> ListRoles() => _roles.Keys.ToList();

    public RoleId GetOrCreateRole(string name)
    {
        if (!_roles.TryGetValue(name, out var id))
        {
            id = new RoleId(_nextRole++);
            _roles[name] = id;
        }
        return id;
    }

    public bool TryGetRoleId(string name, out RoleId id) => _roles.TryGetValue(name, out id);
}

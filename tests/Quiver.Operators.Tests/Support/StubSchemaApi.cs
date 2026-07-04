using Quiver;
using Quiver.Core;

namespace Quiver.Query.Physical.Tests.Support;

/// <summary>
/// プラン構造テスト用の最小 <see cref="ISchemaApi"/> スタブ。
/// 実データベースなしでプランナーが名前を解決できるよう連番 ID を割り当てる。
/// </summary>
internal sealed class StubSchemaApi : ISchemaApi
{
    private int _nextLabel = 1;
    private int _nextRelType = 1;
    private int _nextPropKey = 1;

    private readonly Dictionary<string, LabelId> _labels = new();
    private readonly Dictionary<LabelId, string> _labelNames = new();
    private readonly Dictionary<string, RelationshipTypeId> _relTypes = new();
    private readonly Dictionary<string, (PropertyKeyId Id, PropertyCardinality Card)> _propKeys = new();

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

    public RelationshipTypeId GetOrCreateRelationshipType(string name)
    {
        if (!_relTypes.TryGetValue(name, out var id))
        {
            id = new RelationshipTypeId(_nextRelType++);
            _relTypes[name] = id;
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

    public bool TryGetRelationshipTypeId(string name, out RelationshipTypeId id)
        => _relTypes.TryGetValue(name, out id);

    public bool IndexExists(string indexName) => false;
    public void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind) { }
    public void DropIndex(string indexName) { }
    public IReadOnlyList<IndexInfo> ListIndexes() => [];
    public void CreateFullTextIndex(string indexName, string label, string propertyKey, FullTextIndexOptions? options = null) { }
    public IReadOnlyList<FullTextIndexInfo> ListFullTextIndexes() => [];
    public bool RenameLabel(string oldName, string newName) => false;
    public bool RenamePropertyKey(string oldName, string newName) => false;
    public bool RenameRelationshipType(string oldName, string newName) => false;
    public bool RenameIndex(string oldName, string newName) => false;
    public IReadOnlyList<string> ListLabels() => _labels.Keys.ToList();
    public IReadOnlyList<string> ListRelationshipTypes() => _relTypes.Keys.ToList();
    public IReadOnlyList<string> ListPropertyKeys() => _propKeys.Keys.ToList();

    public HyperedgeTypeId GetOrCreateHyperedgeType(string name) => HyperedgeTypeId.Invalid;
    public string? GetHyperedgeTypeName(HyperedgeTypeId id) => null;
    public bool TryGetHyperedgeTypeId(string name, out HyperedgeTypeId id) { id = default; return false; }
    public IReadOnlyList<string> ListHyperedgeTypes() => [];
    public IReadOnlyList<string> ListRoles() => [];
}

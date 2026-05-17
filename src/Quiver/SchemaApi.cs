using Quiver.Core;
using Quiver.Index;
using Quiver.Stores;

namespace Quiver;

internal sealed class SchemaApi : ISchemaApi
{
    private readonly ITokenStore<LabelId> _labels;
    private readonly ITokenStore<RelationshipTypeId> _relTypes;
    private readonly ITokenStore<PropertyKeyId> _propKeys;
    private readonly IIndexManager _indexManager;

    internal SchemaApi(
        ITokenStore<LabelId> labels,
        ITokenStore<RelationshipTypeId> relTypes,
        ITokenStore<PropertyKeyId> propKeys,
        IIndexManager indexManager)
    {
        _labels = labels;
        _relTypes = relTypes;
        _propKeys = propKeys;
        _indexManager = indexManager;
    }

    public LabelId GetOrCreateLabel(string name) => _labels.GetOrCreate(name);
    public RelationshipTypeId GetOrCreateRelationshipType(string name) => _relTypes.GetOrCreate(name);
    public PropertyKeyId GetOrCreatePropertyKey(string name) => _propKeys.GetOrCreate(name);

    public string? GetLabelName(LabelId id) => id.IsValid ? _labels.GetName(id) : null;

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
        // PW-18 follow-up: バインディングを登録し、MergeNode が自動でこのインデックスを
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

    // PW-18 follow-up: IndexKind は IIndexManager の表現外なので SchemaApi 側で保持する。
    private readonly Dictionary<string, IndexKind> _indexKinds = new(StringComparer.Ordinal);
}

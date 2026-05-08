using GraphDb.Engine.Core;
using GraphDb.Engine.Index;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine;

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
    }

    public void DropIndex(string indexName) => _indexManager.DropIndex(indexName);

    public IReadOnlyList<IndexInfo> ListIndexes()
        => _indexManager.ListIndexes()
            .Select(n => new IndexInfo(n, "", "", IndexKind.StringEquality, 0))
            .ToList();
}

using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine;

internal sealed class GraphTransaction : IGraphTransaction
{
    private readonly ITransaction _inner;
    private readonly ITokenStore<LabelId> _labelTokens;
    private readonly ITokenStore<RelationshipTypeId> _relTypeTokens;
    private readonly ITokenStore<PropertyKeyId> _propKeyTokens;

    internal GraphTransaction(
        ITransaction inner,
        ITokenStore<LabelId> labelTokens,
        ITokenStore<RelationshipTypeId> relTypeTokens,
        ITokenStore<PropertyKeyId> propKeyTokens)
    {
        _inner = inner;
        _labelTokens = labelTokens;
        _relTypeTokens = relTypeTokens;
        _propKeyTokens = propKeyTokens;
    }

    public TransactionId Id => _inner.Id;
    public TransactionState State => _inner.State;

    // ========== ノード操作 ==========

    public NodeId CreateNode(string label)
        => CreateNode(_labelTokens.GetOrCreate(label));

    public NodeId CreateNode(LabelId labelId)
        => _inner.Nodes.Allocate(labelId);

    public void DeleteNode(NodeId nodeId)
    {
        // Collect all relationships first, then delete them
        var firstRelId = _inner.Nodes.Read(nodeId).FirstRelationshipId;
        var toDelete = new List<RelationshipId>();
        var relId = firstRelId;
        while (relId.IsValid)
        {
            toDelete.Add(relId);
            var rel = _inner.Relationships.Read(relId);
            relId = rel.Source == nodeId ? rel.SourceNext : rel.TargetNext;
        }
        foreach (var rid in toDelete)
            _inner.Relationships.Delete(_inner.Nodes, rid);

        _inner.Nodes.Free(nodeId);
    }

    public bool NodeExists(NodeId nodeId)
        => _inner.Nodes.Read(nodeId).InUse;

    // ========== リレーション操作 ==========

    public RelationshipId CreateRelationship(NodeId source, NodeId target, string type)
        => CreateRelationship(source, target, _relTypeTokens.GetOrCreate(type));

    public RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId)
        => _inner.Relationships.Create(_inner.Nodes, source, target, typeId);

    public void DeleteRelationship(RelationshipId relId)
        => _inner.Relationships.Delete(_inner.Nodes, relId);

    // ========== プロパティ操作 ==========

    public void SetProperty(NodeId nodeId, string key, in PropertyValue value)
    {
        var keyId = _propKeyTokens.GetOrCreate(key);
        SetNodeProperty(nodeId, keyId, in value);
    }

    public void SetProperty(RelationshipId relId, string key, in PropertyValue value)
    {
        // Phase 1: relationship properties not yet supported — store as node property workaround
        throw new NotSupportedException("Relationship properties not yet implemented in Phase 1.");
    }

    private void SetNodeProperty(NodeId nodeId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;

        // Remove old value if exists
        var newFirst = firstPropId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                newFirst = _inner.Properties.Delete(propEnum.Current.Id, newFirst);
                break;
            }
        }

        var newPropId = _inner.Properties.Create(keyId, in value, newFirst);
        var wh = _inner.Nodes.Write(nodeId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    public void RemoveProperty(NodeId nodeId, string key)
    {
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;

        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Nodes.Write(nodeId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    public PropertyValue GetProperty(NodeId nodeId, string key)
    {
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId) return prop.Value;
        }
        return default;
    }

    public PropertyValue GetProperty(RelationshipId relId, string key)
        => throw new NotSupportedException("Relationship properties not yet implemented in Phase 1.");

    public bool HasProperty(NodeId nodeId, string key)
    {
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId) return true;
        }
        return false;
    }

    public PropertyEnumerator EnumerateProperties(NodeId nodeId)
    {
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        return _inner.Properties.Enumerate(firstPropId);
    }

    // ========== トラバーサル ==========

    public RelationshipEnumerator EnumerateRelationships(
        NodeId nodeId,
        Direction direction = Direction.Both,
        string? typeFilter = null)
    {
        if (typeFilter != null && _relTypeTokens.TryGet(typeFilter, out var typeId))
            return _inner.Relationships.EnumerateNeighbors(nodeId, _inner.Nodes, typeId, direction);

        return _inner.Relationships.EnumerateNeighbors(nodeId, _inner.Nodes);
    }

    // ========== インデックス ==========

    public void IndexInsert(string indexName, string key, NodeId nodeId)
        => _inner.Indexes.CreateStringIndex(indexName).Insert(key, nodeId.Value);

    public void IndexInsert(string indexName, long key, NodeId nodeId)
        => _inner.Indexes.CreateInt64Index(indexName).Insert(key, nodeId.Value);

    public void IndexInsert(string indexName, double key, NodeId nodeId)
        => _inner.Indexes.CreateDoubleIndex(indexName).Insert(key, nodeId.Value);

    public NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key)
        => throw new NotImplementedException();

    public NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive)
        => throw new NotImplementedException();

    // ========== 物理プラン実行 ==========

    public QueryResult Execute(IPhysicalOperator plan)
    {
        plan.Open(_inner);
        var rows = new List<QueryRow>();

        while (plan.MoveNext())
        {
            var cur = plan.Current;
            var slots = new TupleSlot[cur.ColumnCount];
            byte[]?[]? byteData = null;

            for (int i = 0; i < cur.ColumnCount; i++)
            {
                slots[i] = cur[i];
                if (slots[i].Type is TupleSlotType.Utf8String or TupleSlotType.Bytes)
                {
                    byteData ??= new byte[]?[cur.ColumnCount];
                    byteData[i] = plan.GetBytes(i).ToArray();
                }
            }
            rows.Add(new QueryRow(slots, byteData));
        }

        var schema = plan.Schema;
        var stats = plan.Statistics;
        plan.Dispose();
        return new QueryResult(schema, stats, rows);
    }

    public void Commit() => _inner.Commit();
    public void Rollback() => _inner.Abort();
    public void Dispose() => _inner.Dispose();
}

using Quiver.Core;
using Quiver.Logical;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

internal sealed class GraphTransaction : IGraphTransaction
{
    private readonly ITransaction _inner;
    private readonly ITokenStore<LabelId> _labelTokens;
    private readonly ITokenStore<RelationshipTypeId> _relTypeTokens;
    private readonly ITokenStore<PropertyKeyId> _propKeyTokens;
    // BA-7: when non-null we buffer every public mutation and hand the batch
    // to the sink after the underlying transaction has durably committed.
    private readonly ILogicalMutationSink? _logicalSink;
    private List<LogicalMutation>? _logicalBuffer;

    internal GraphTransaction(
        ITransaction inner,
        ITokenStore<LabelId> labelTokens,
        ITokenStore<RelationshipTypeId> relTypeTokens,
        ITokenStore<PropertyKeyId> propKeyTokens,
        bool isReadOnly = false,
        ILogicalMutationSink? logicalSink = null)
    {
        _inner = inner;
        _labelTokens = labelTokens;
        _relTypeTokens = relTypeTokens;
        _propKeyTokens = propKeyTokens;
        IsReadOnly = isReadOnly;
        _logicalSink = logicalSink;
        if (_logicalSink != null)
        {
            // Hand the buffer to the sink only after the WAL flush returned —
            // OnCommitted hooks do not fire on rollback or commit failure.
            _inner.OnCommitted(FlushLogicalBuffer);
        }
    }

    private void RecordLogical(in LogicalMutation mutation)
    {
        if (_logicalSink == null) return;
        (_logicalBuffer ??= new List<LogicalMutation>()).Add(mutation);
    }

    private void FlushLogicalBuffer()
    {
        if (_logicalSink == null || _logicalBuffer == null || _logicalBuffer.Count == 0) return;
        _logicalSink.OnCommitted(_inner.Id, _logicalBuffer);
    }

    public TransactionId Id => _inner.Id;
    public TransactionState State => _inner.State;
    public bool IsReadOnly { get; }

    // ========== ノード操作 ==========

    public NodeId CreateNode(string label)
    {
        var labelId = _labelTokens.GetOrCreate(label);
        var nodeId = _inner.Nodes.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateNode(nodeId, label));
        return nodeId;
    }

    public NodeId CreateNode(LabelId labelId)
    {
        var nodeId = _inner.Nodes.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateNode(nodeId, _labelTokens.GetName(labelId)));
        return nodeId;
    }

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
            DeleteRelationship(rid);

        _inner.Nodes.Free(nodeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteNode(nodeId));
    }

    public bool NodeExists(NodeId nodeId)
        => _inner.Nodes.Read(nodeId).InUse;

    // ========== MERGE (GC-5) ==========

    public (NodeId Id, bool Created) MergeNode(string label, string matchKey, in PropertyValue matchValue)
    {
        var labelId = _labelTokens.GetOrCreate(label);

        // Scan-and-compare via the backend access path. We skip the scan when
        // the property key has never been observed — no node can carry an
        // unminted key, so the match must be a miss.
        if (_propKeyTokens.TryGet(matchKey, out var keyId))
        {
            foreach (var nodeId in _inner.Access.ScanNodes(_inner, labelId))
            {
                var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
                if (!firstPropId.IsValid) continue;
                var propEnum = _inner.Properties.Enumerate(firstPropId);
                while (propEnum.MoveNext())
                {
                    var cur = propEnum.Current;
                    if (cur.KeyId != keyId) continue;
                    if (PropertyValueEqualityHelper.AreEqual(cur.Value, in matchValue))
                        return (nodeId, false);
                    break;
                }
            }
        }

        var newId = _inner.Nodes.Allocate(labelId);
        SetNodeProperty(newId, _propKeyTokens.GetOrCreate(matchKey), in matchValue);
        return (newId, true);
    }

    // ========== リレーション操作 ==========

    public RelationshipId CreateRelationship(NodeId source, NodeId target, string type)
    {
        var typeId = _relTypeTokens.GetOrCreate(type);
        var relId = _inner.Relationships.Create(_inner.Nodes, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateRelationship(relId, source, target, type));
        return relId;
    }

    public RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId)
    {
        var relId = _inner.Relationships.Create(_inner.Nodes, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateRelationship(
                relId, source, target, _relTypeTokens.GetName(typeId)));
        return relId;
    }

    public void DeleteRelationship(RelationshipId relId)
    {
        FreeRelationshipProperties(relId);
        // PW-14: if this id falls inside the immutable base view it still
        // shows up in the adjacency block — record a tombstone so subsequent
        // expand cursors skip it. The store no-ops for delta ids.
        _inner.AdjacencyBlocks?.Tombstone(relId);
        _inner.Relationships.Delete(_inner.Nodes, relId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteRelationship(relId));
    }

    private void FreeRelationshipProperties(RelationshipId relId)
    {
        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var toDelete = new List<PropertyId>();
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
            toDelete.Add(propEnum.Current.Id);
        var currentFirst = firstPropId;
        foreach (var pid in toDelete)
            currentFirst = _inner.Properties.Delete(pid, currentFirst);
    }

    // ========== プロパティ操作 ==========

    public void SetProperty(NodeId nodeId, string key, in PropertyValue value)
    {
        var keyId = _propKeyTokens.GetOrCreate(key);
        // BA-7: capture before SetNodeProperty mutates the chain — value is a
        // ref struct, so the heap copy lives in LogicalPropertyValue.
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetNodeProperty(nodeId, key, in captured));
        }
        SetNodeProperty(nodeId, keyId, in value);
    }

    public void SetProperty(RelationshipId relId, string key, in PropertyValue value)
    {
        var keyId = _propKeyTokens.GetOrCreate(key);
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetRelationshipProperty(relId, key, in captured));
        }
        SetRelationshipProperty(relId, keyId, in value);
    }

    private void SetRelationshipProperty(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
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
        var wh = _inner.Relationships.Write(relId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
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
                if (_logicalSink != null)
                    RecordLogical(LogicalMutation.RemoveNodeProperty(nodeId, key));
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
    {
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId) return prop.Value;
        }
        return default;
    }

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
    {
        IEnumerable<long> values = key.Type switch
        {
            PropertyValueType.Int32 or PropertyValueType.Int64 or PropertyValueType.Bool =>
                _inner.Indexes.CreateInt64Index(indexName).SeekValues(key.Int64Value),
            PropertyValueType.Double =>
                _inner.Indexes.CreateDoubleIndex(indexName).SeekValues(key.DoubleValue),
            PropertyValueType.String =>
                _inner.Indexes.CreateStringIndex(indexName).SeekValues(
                    System.Text.Encoding.UTF8.GetString(key.Utf8StringValue)),
            _ => [],
        };
        return new NodeIdEnumerator(values);
    }

    public NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive)
    {
        IEnumerable<long> values;
        switch (from.Type)
        {
            case PropertyValueType.Int32:
            case PropertyValueType.Int64:
            case PropertyValueType.Bool:
                values = _inner.Indexes.CreateInt64Index(indexName).RangeValues(
                    from.Int64Value, fromInclusive, to.Int64Value, toInclusive);
                break;
            case PropertyValueType.Double:
                values = _inner.Indexes.CreateDoubleIndex(indexName).RangeValues(
                    from.DoubleValue, fromInclusive, to.DoubleValue, toInclusive);
                break;
            case PropertyValueType.String:
                string fromStr = System.Text.Encoding.UTF8.GetString(from.Utf8StringValue);
                string toStr   = System.Text.Encoding.UTF8.GetString(to.Utf8StringValue);
                values = _inner.Indexes.CreateStringIndex(indexName).RangeValues(
                    fromStr, fromInclusive, toStr, toInclusive);
                break;
            default:
                values = [];
                break;
        }
        return new NodeIdEnumerator(values);
    }

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

    public IQueryCursor ExecuteCursor(IPhysicalOperator plan)
    {
        plan.Open(_inner);
        return new PhysicalOperatorCursor(plan);
    }

    public IAdjacencyBlockStore? AdjacencyBlocks => _inner.AdjacencyBlocks;

    public void Commit() => _inner.Commit();
    public void Rollback() => _inner.Abort();
    public void Dispose() => _inner.Dispose();

    // VEC-3: post-commit / post-rollback hook registration delegates to the
    // underlying transaction so users can register hooks via IGraphTransaction.
    public void OnCommitted(Action callback) => _inner.OnCommitted(callback);
    public void OnRolledBack(Action callback) => _inner.OnRolledBack(callback);
}

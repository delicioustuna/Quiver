using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine;

public interface IGraphTransaction : IDisposable
{
    TransactionId Id { get; }
    TransactionState State { get; }

    // ノード操作
    NodeId CreateNode(string label);
    NodeId CreateNode(LabelId labelId);
    void DeleteNode(NodeId nodeId);
    bool NodeExists(NodeId nodeId);

    // リレーション操作
    RelationshipId CreateRelationship(NodeId source, NodeId target, string type);
    RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId);
    void DeleteRelationship(RelationshipId relId);

    // プロパティ操作
    void SetProperty(NodeId nodeId, string key, in PropertyValue value);
    void SetProperty(RelationshipId relId, string key, in PropertyValue value);
    void RemoveProperty(NodeId nodeId, string key);
    PropertyValue GetProperty(NodeId nodeId, string key);
    PropertyValue GetProperty(RelationshipId relId, string key);
    bool HasProperty(NodeId nodeId, string key);
    PropertyEnumerator EnumerateProperties(NodeId nodeId);

    // トラバーサル
    RelationshipEnumerator EnumerateRelationships(
        NodeId nodeId,
        Direction direction = Direction.Both,
        string? typeFilter = null);

    // インデックス
    NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key);
    NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive);

    // 物理プラン実行
    QueryResult Execute(IPhysicalOperator plan);

    void Commit();
    void Rollback();
}

public ref struct NodeIdEnumerator
{
    public bool MoveNext() => throw new NotImplementedException();
    public NodeId Current => throw new NotImplementedException();
    public void Dispose() { }
}

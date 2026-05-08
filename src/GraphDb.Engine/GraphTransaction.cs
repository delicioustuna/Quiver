using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine;

internal sealed class GraphTransaction : IGraphTransaction
{
    private readonly ITransaction _inner;

    public GraphTransaction(ITransaction inner) => _inner = inner;

    public TransactionId Id => _inner.Id;
    public TransactionState State => _inner.State;

    public NodeId CreateNode(string label) => throw new NotImplementedException();
    public NodeId CreateNode(LabelId labelId) => throw new NotImplementedException();
    public void DeleteNode(NodeId nodeId) => throw new NotImplementedException();
    public bool NodeExists(NodeId nodeId) => throw new NotImplementedException();

    public RelationshipId CreateRelationship(NodeId source, NodeId target, string type) => throw new NotImplementedException();
    public RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId) => throw new NotImplementedException();
    public void DeleteRelationship(RelationshipId relId) => throw new NotImplementedException();

    public void SetProperty(NodeId nodeId, string key, in PropertyValue value) => throw new NotImplementedException();
    public void SetProperty(RelationshipId relId, string key, in PropertyValue value) => throw new NotImplementedException();
    public void RemoveProperty(NodeId nodeId, string key) => throw new NotImplementedException();
    public PropertyValue GetProperty(NodeId nodeId, string key) => throw new NotImplementedException();
    public PropertyValue GetProperty(RelationshipId relId, string key) => throw new NotImplementedException();
    public bool HasProperty(NodeId nodeId, string key) => throw new NotImplementedException();
    public PropertyEnumerator EnumerateProperties(NodeId nodeId) => throw new NotImplementedException();

    public RelationshipEnumerator EnumerateRelationships(NodeId nodeId, Direction direction = Direction.Both, string? typeFilter = null)
        => throw new NotImplementedException();

    public NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key) => throw new NotImplementedException();
    public NodeIdEnumerator RangeIndex(string indexName, in PropertyValue from, bool fromInclusive, in PropertyValue to, bool toInclusive)
        => throw new NotImplementedException();

    public QueryResult Execute(IPhysicalOperator plan) => throw new NotImplementedException();

    public void Commit() => _inner.Commit();
    public void Rollback() => _inner.Abort();
    public void Dispose() => _inner.Dispose();
}

using GraphDb.Engine.Core;

namespace GraphDb.Engine.Client;

public interface IGraphRelationship<TSelf> where TSelf : IGraphRelationship<TSelf>
{
    static abstract string GraphType { get; }
    static abstract RelationshipId Insert(IGraphTransaction tx, NodeId from, NodeId to, TSelf entity);
    static abstract TSelf Load(IGraphTransaction tx, RelationshipId id);
    static abstract void Update(IGraphTransaction tx, RelationshipId id, TSelf entity);
    static abstract void Delete(IGraphTransaction tx, RelationshipId id);
}

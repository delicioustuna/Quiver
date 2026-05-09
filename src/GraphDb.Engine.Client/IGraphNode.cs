using GraphDb.Engine.Core;

namespace GraphDb.Engine.Client;

public interface IGraphNode<TSelf> where TSelf : IGraphNode<TSelf>
{
    static abstract string GraphLabel { get; }
    static abstract NodeId Insert(IGraphTransaction tx, TSelf entity);
    static abstract NodeId InsertIndexed(IGraphTransaction tx, TSelf entity);
    static abstract TSelf Load(IGraphTransaction tx, NodeId id);
    static abstract void Update(IGraphTransaction tx, NodeId id, TSelf entity);
    static abstract void Delete(IGraphTransaction tx, NodeId id);
}

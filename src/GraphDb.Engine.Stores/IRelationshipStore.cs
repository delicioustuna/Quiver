using GraphDb.Engine.Core;

namespace GraphDb.Engine.Stores;

public interface IRelationshipStore
{
    RelationshipId Create(INodeStore nodeStore, NodeId source, NodeId target, RelationshipTypeId type);
    void Delete(INodeStore nodeStore, RelationshipId relId);
    RelationshipReadHandle Read(RelationshipId relId);
    RelationshipWriteHandle Write(RelationshipId relId);
    RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore);
    RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore, RelationshipTypeId type, Direction direction);
    long InUseCount { get; }
}

public readonly ref struct RelationshipReadHandle
{
    private readonly RelationshipId _id;
    private readonly bool _inUse;
    private readonly NodeId _source;
    private readonly NodeId _target;
    private readonly RelationshipTypeId _type;
    private readonly RelationshipId _sourcePrev;
    private readonly RelationshipId _sourceNext;
    private readonly RelationshipId _targetPrev;
    private readonly RelationshipId _targetNext;

    public RelationshipId Id => _id;
    public bool InUse => _inUse;
    public NodeId Source => _source;
    public NodeId Target => _target;
    public RelationshipTypeId Type => _type;
    public RelationshipId SourcePrev => _sourcePrev;
    public RelationshipId SourceNext => _sourceNext;
    public RelationshipId TargetPrev => _targetPrev;
    public RelationshipId TargetNext => _targetNext;

    internal RelationshipReadHandle(
        RelationshipId id, bool inUse,
        NodeId source, NodeId target, RelationshipTypeId type,
        RelationshipId sourcePrev, RelationshipId sourceNext,
        RelationshipId targetPrev, RelationshipId targetNext)
    {
        _id = id; _inUse = inUse;
        _source = source; _target = target; _type = type;
        _sourcePrev = sourcePrev; _sourceNext = sourceNext;
        _targetPrev = targetPrev; _targetNext = targetNext;
    }

    public void Dispose() { }
}

public ref struct RelationshipWriteHandle
{
    public NodeId Source;
    public NodeId Target;
    public RelationshipTypeId Type;
    public RelationshipId SourcePrev;
    public RelationshipId SourceNext;
    public RelationshipId TargetPrev;
    public RelationshipId TargetNext;

    public void Dispose() { }
}

public ref struct RelationshipEnumerator
{
    public bool MoveNext() => throw new NotImplementedException();
    public RelationshipReadHandle Current => throw new NotImplementedException();
    public void Dispose() { }
}

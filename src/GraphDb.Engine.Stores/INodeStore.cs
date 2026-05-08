using GraphDb.Engine.Core;

namespace GraphDb.Engine.Stores;

public interface INodeStore
{
    NodeId Allocate(LabelId labelId);
    void Free(NodeId nodeId);
    NodeReadHandle Read(NodeId nodeId);
    NodeWriteHandle Write(NodeId nodeId);
    IEnumerable<NodeId> Scan();
    long InUseCount { get; }
}

public readonly ref struct NodeReadHandle
{
    private readonly NodeId _id;
    private readonly bool _inUse;
    private readonly RelationshipId _firstRelId;
    private readonly PropertyId _firstPropId;
    private readonly LabelId _label;

    public NodeId Id => _id;
    public bool InUse => _inUse;
    public RelationshipId FirstRelationshipId => _firstRelId;
    public PropertyId FirstPropertyId => _firstPropId;
    public LabelId Label => _label;

    internal NodeReadHandle(NodeId id, bool inUse, RelationshipId firstRelId, PropertyId firstPropId, LabelId label)
    {
        _id = id;
        _inUse = inUse;
        _firstRelId = firstRelId;
        _firstPropId = firstPropId;
        _label = label;
    }

    public void Dispose() { }
}

public ref struct NodeWriteHandle
{
    public RelationshipId FirstRelationshipId;
    public PropertyId FirstPropertyId;
    public LabelId Label;

    public void Dispose() { }
}

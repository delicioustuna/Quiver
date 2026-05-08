using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Stores;

internal sealed class RelationshipStore : IRelationshipStore
{
    public const int RecordSize = 40;

    private readonly IPagedFile _file;

    public RelationshipStore(IPagedFile file)
    {
        _file = file;
    }

    public long InUseCount => throw new NotImplementedException();

    public RelationshipId Create(INodeStore nodeStore, NodeId source, NodeId target, RelationshipTypeId type)
        => throw new NotImplementedException();

    public void Delete(INodeStore nodeStore, RelationshipId relId)
        => throw new NotImplementedException();

    public RelationshipReadHandle Read(RelationshipId relId)
        => throw new NotImplementedException();

    public RelationshipWriteHandle Write(RelationshipId relId)
        => throw new NotImplementedException();

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore)
        => throw new NotImplementedException();

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore, RelationshipTypeId type, Direction direction)
        => throw new NotImplementedException();
}

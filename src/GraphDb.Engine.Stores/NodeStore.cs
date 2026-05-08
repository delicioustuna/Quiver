using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Stores;

internal sealed class NodeStore : INodeStore
{
    public const int RecordSize = 15;

    private readonly IPagedFile _file;

    public NodeStore(IPagedFile file)
    {
        _file = file;
    }

    public long InUseCount => throw new NotImplementedException();

    public NodeId Allocate(LabelId labelId) => throw new NotImplementedException();
    public void Free(NodeId nodeId) => throw new NotImplementedException();
    public NodeReadHandle Read(NodeId nodeId) => throw new NotImplementedException();
    public NodeWriteHandle Write(NodeId nodeId) => throw new NotImplementedException();
    public IEnumerable<NodeId> Scan() => throw new NotImplementedException();
}

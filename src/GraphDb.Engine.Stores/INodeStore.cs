using System.Buffers.Binary;
using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;

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
        _id = id; _inUse = inUse; _firstRelId = firstRelId; _firstPropId = firstPropId; _label = label;
    }

    public void Dispose() { }
}

// Offsets: Flags(0,1) FirstRelId(1,6) FirstPropId(7,6) LabelId(13,2) — 15 bytes
public ref struct NodeWriteHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private Span<byte> _rec;

    internal NodeWriteHandle(IPagedFile file, PageId pageId, Span<byte> rec)
    {
        _file = file; _pageId = pageId; _rec = rec;
    }

    public RelationshipId FirstRelationshipId
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[1..]));
        set => RecordHelpers.WriteInt48(_rec[1..], value.Value);
    }

    public PropertyId FirstPropertyId
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[7..]));
        set => RecordHelpers.WriteInt48(_rec[7..], value.Value);
    }

    public LabelId Label
    {
        readonly get => new(BinaryPrimitives.ReadInt16LittleEndian(_rec[13..]));
        set => BinaryPrimitives.WriteInt16LittleEndian(_rec[13..], (short)value.Value);
    }

    public void Dispose() => _file.UnpinDirty(_pageId, 0);
}

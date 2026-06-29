using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

internal interface INodeStore
{
    NodeId Allocate(LabelId labelId);
    void Free(NodeId nodeId);
    NodeReadHandle Read(NodeId nodeId);
    NodeWriteHandle Write(NodeId nodeId);
    IEnumerable<NodeId> Scan();
    long InUseCount { get; }

    /// <summary>
    /// slot <paramref name="localId"/> の現在の世代 (incarnation)。範囲外 / 負は -1。
    /// 索引値 (<see cref="Quiver.Core.EntityRef"/>) の世代照合に使う。
    /// </summary>
    int CurrentGeneration(long localId);

    // ノード粒度の inline property。小さい値は node record へ inline 格納し
    // get/has/set/remove を O(small) 化する。inline 不可な値 (大きい string/bytes) は false を返し、
    // 呼出側 (GraphTransaction) が overflow チェーン (PropertyStore) へ回す。inline を持たない実装
    // (旧 NodeStore / Stub) は false を返して全 property を overflow に委ねる (graceful degrade)。

    /// <summary>visible 版の inline 領域から property を読む。inline に無ければ false。</summary>
    bool TryGetInlineProperty(NodeId nodeId, PropertyKeyId keyId, out PropertyValue value);

    /// <summary>visible 版の inline 領域に keyId があるか。</summary>
    bool HasInlineProperty(NodeId nodeId, PropertyKeyId keyId);

    /// <summary>inline property を set (copy-on-write)。inline 不可 / 予算超過なら false。</summary>
    bool SetInlineProperty(NodeId nodeId, PropertyKeyId keyId, in PropertyValue value);

    /// <summary>inline property を remove (copy-on-write)。inline に無ければ false。</summary>
    bool RemoveInlineProperty(NodeId nodeId, PropertyKeyId keyId);

    /// <summary>inline + overflow チェーンを結合した property 列挙子を返す。</summary>
    PropertyEnumerator EnumerateProperties(NodeId nodeId, IPropertyStore overflowStore);
}

internal readonly ref struct NodeReadHandle
{
    private readonly NodeId _id;
    private readonly bool _inUse;
    private readonly RelationshipId _firstRelId;
    private readonly PropertyId _firstPropId;
    private readonly LabelId _label;
    private readonly long _xmin;
    private readonly long _xmax;

    public NodeId Id => _id;
    public bool InUse => _inUse;
    public RelationshipId FirstRelationshipId => _firstRelId;
    public PropertyId FirstPropertyId => _firstPropId;
    public LabelId Label => _label;

    /// <summary>record を生成したトランザクション ID (sidecar 由来)。</summary>
    public long Xmin => _xmin;

    /// <summary>record を論理削除したトランザクション ID (sidecar 由来、0 = 生存)。</summary>
    public long Xmax => _xmax;

    internal NodeReadHandle(NodeId id, bool inUse, RelationshipId firstRelId, PropertyId firstPropId, LabelId label, long xmin = 0, long xmax = 0)
    {
        _id = id; _inUse = inUse; _firstRelId = firstRelId; _firstPropId = firstPropId; _label = label;
        _xmin = xmin; _xmax = xmax;
    }

    public void Dispose() { }
}

// v3 レイアウト: Flags(0,1) FirstRelId(1,6) FirstPropId(7,6) LabelId(13,2) — 15 bytes
// (Xmin/Xmax は NodeVersionMeta sidecar に移管)
internal ref struct NodeWriteHandle
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
        set => RecordHelpers.WriteInt48(_rec[1..], value.Sequence); // Int48 は Sequence
    }

    public PropertyId FirstPropertyId
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[7..]));
        set => RecordHelpers.WriteInt48(_rec[7..], value.Sequence); // Int48 は Sequence
    }

    public LabelId Label
    {
        readonly get => new(BinaryPrimitives.ReadInt16LittleEndian(_rec[13..]));
        set => BinaryPrimitives.WriteInt16LittleEndian(_rec[13..], (short)value.Value);
    }

    public void Dispose() => _file.UnpinDirty(_pageId, 0);
}

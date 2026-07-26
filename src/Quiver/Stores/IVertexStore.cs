using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

internal interface IVertexStore
{
    VertexId Allocate(LabelId labelId);
    void Free(VertexId vertexId);
    VertexReadHandle Read(VertexId vertexId);
    VertexWriteHandle Write(VertexId vertexId);
    IEnumerable<VertexId> Scan();
    long InUseCount { get; }

    /// <summary>
    /// slot <paramref name="localId"/> の現在の世代 (incarnation)。範囲外 / 負は -1。
    /// property owner や導出候補の世代照合に使う。
    /// </summary>
    int CurrentGeneration(long localId);

    /// <summary>owner-bound property version chain の列挙子を返す。</summary>
    PropertyCursor EnumerateProperties(VertexId vertexId, IPropertyStore overflowStore);
}

internal interface ITransactionVertexStore
{
    VertexId Allocate(LabelId labelId, TransactionId transactionId);
    void Free(VertexId vertexId, TransactionId transactionId);
    VertexReadHandle Read(VertexId vertexId, VersionVisible visibility);
    IEnumerable<VertexId> Scan(VersionVisible visibility);
}

internal readonly ref struct VertexReadHandle
{
    private readonly VertexId _id;
    private readonly bool _inUse;
    private readonly EdgeId _firstEdgeId;
    private readonly PropertyVersionRef _firstPropertyRef;
    private readonly LabelId _label;
    private readonly long _xmin;
    private readonly long _xmax;

    public VertexId Id => _id;
    public bool InUse => _inUse;
    public EdgeId FirstEdgeId => _firstEdgeId;
    public PropertyVersionRef FirstPropertyRef => _firstPropertyRef;
    public LabelId Label => _label;

    /// <summary>record を生成したトランザクション ID (sidecar 由来)。</summary>
    public long Xmin => _xmin;

    /// <summary>record を論理削除したトランザクション ID (sidecar 由来、0 = 生存)。</summary>
    public long Xmax => _xmax;

    internal VertexReadHandle(VertexId id, bool inUse, EdgeId firstEdgeId, PropertyVersionRef firstPropertyRef, LabelId label, long xmin = 0, long xmax = 0)
    {
        _id = id; _inUse = inUse; _firstEdgeId = firstEdgeId; _firstPropertyRef = firstPropertyRef; _label = label;
        _xmin = xmin; _xmax = xmax;
    }

    public void Dispose() { }
}

// v3 レイアウト: Flags(0,1) FirstEdgeId(1,6) FirstPropertyRef(7,6) LabelId(13,2) — 15 bytes
// (Xmin/Xmax は VertexVersionMeta sidecar に移管)
internal ref struct VertexWriteHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private Span<byte> _rec;

    internal VertexWriteHandle(IPagedFile file, PageId pageId, Span<byte> rec)
    {
        _file = file; _pageId = pageId; _rec = rec;
    }

    public EdgeId FirstEdgeId
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[1..]));
        set => RecordHelpers.WriteInt48(_rec[1..], value.Sequence); // Int48 は Sequence
    }

    public PropertyVersionRef FirstPropertyRef
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

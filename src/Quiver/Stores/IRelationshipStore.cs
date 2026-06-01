using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

internal interface IRelationshipStore
{
    RelationshipId Create(INodeStore nodeStore, NodeId source, NodeId target, RelationshipTypeId type);
    void Delete(INodeStore nodeStore, RelationshipId relId);
    RelationshipReadHandle Read(RelationshipId relId);
    RelationshipWriteHandle Write(RelationshipId relId);
    RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore);
    RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore, RelationshipTypeId type, Direction direction);
    long InUseCount { get; }

    /// <summary>
    /// 生存中のすべての <see cref="RelationshipId"/> をストア順 (id 0 → hwm-1) で列挙する。
    /// PW-17 の <c>RelationshipScanExpandOperator</c> が大規模 frontier 展開で利用する経路で、
    /// ノード毎リンクリストを多数辿るより順次スキャンの方が安価なケース向け。
    /// </summary>
    IEnumerable<RelationshipId> Scan();
}

// RelRecord レイアウト (48 バイト):
// 0 Flags(1) 1 Source(6) 7 Target(6) 13 TypeId(2) 15 SrcPrev(6) 21 SrcNext(6) 27 TgtPrev(6) 33 TgtNext(6) 39 FirstPropId(6) 45 Pad(3)
public readonly ref struct RelationshipReadHandle
{
    private readonly RelationshipId _id;
    private readonly bool _inUse;
    private readonly NodeId _source;
    private readonly NodeId _target;
    private readonly RelationshipTypeId _type;
    private readonly RelationshipId _srcPrev;
    private readonly RelationshipId _srcNext;
    private readonly RelationshipId _tgtPrev;
    private readonly RelationshipId _tgtNext;
    private readonly PropertyId _firstPropId;

    public RelationshipId Id => _id;
    public bool InUse => _inUse;
    public NodeId Source => _source;
    public NodeId Target => _target;
    public RelationshipTypeId Type => _type;
    public RelationshipId SourcePrev => _srcPrev;
    public RelationshipId SourceNext => _srcNext;
    public RelationshipId TargetPrev => _tgtPrev;
    public RelationshipId TargetNext => _tgtNext;
    public PropertyId FirstPropertyId => _firstPropId;

    internal RelationshipReadHandle(
        RelationshipId id, bool inUse, NodeId source, NodeId target, RelationshipTypeId type,
        RelationshipId srcPrev, RelationshipId srcNext, RelationshipId tgtPrev, RelationshipId tgtNext,
        PropertyId firstPropId)
    {
        _id = id; _inUse = inUse; _source = source; _target = target; _type = type;
        _srcPrev = srcPrev; _srcNext = srcNext; _tgtPrev = tgtPrev; _tgtNext = tgtNext;
        _firstPropId = firstPropId;
    }

    public void Dispose() { }
}

internal ref struct RelationshipWriteHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private Span<byte> _rec;

    internal RelationshipWriteHandle(IPagedFile file, PageId pageId, Span<byte> rec)
    {
        _file = file; _pageId = pageId; _rec = rec;
    }

    public NodeId Source
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[1..]));
        set => RecordHelpers.WriteInt48(_rec[1..], value.Value);
    }
    public NodeId Target
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[7..]));
        set => RecordHelpers.WriteInt48(_rec[7..], value.Value);
    }
    public RelationshipTypeId Type
    {
        readonly get => new(BinaryPrimitives.ReadInt16LittleEndian(_rec[13..]));
        set => BinaryPrimitives.WriteInt16LittleEndian(_rec[13..], (short)value.Value);
    }
    public RelationshipId SourcePrev
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[15..]));
        set => RecordHelpers.WriteInt48(_rec[15..], value.Value);
    }
    public RelationshipId SourceNext
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[21..]));
        set => RecordHelpers.WriteInt48(_rec[21..], value.Value);
    }
    public RelationshipId TargetPrev
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[27..]));
        set => RecordHelpers.WriteInt48(_rec[27..], value.Value);
    }
    public RelationshipId TargetNext
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[33..]));
        set => RecordHelpers.WriteInt48(_rec[33..], value.Value);
    }
    public PropertyId FirstPropertyId
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[39..]));
        set => RecordHelpers.WriteInt48(_rec[39..], value.Value);
    }

    public void Dispose() => _file.UnpinDirty(_pageId, 0);
}

public ref struct RelationshipEnumerator
{
    private readonly IRelationshipStore _store;
    private readonly NodeId _nodeId;
    private readonly RelationshipTypeId _filterType;
    private readonly Direction _direction;
    private readonly bool _hasFilter;
    private RelationshipId _currentId;
    private RelationshipReadHandle _current;
    private bool _started;

    internal RelationshipEnumerator(IRelationshipStore store, NodeId nodeId, RelationshipId firstRelId)
    {
        _store = store; _nodeId = nodeId; _currentId = firstRelId;
        _filterType = default; _direction = Direction.Both; _hasFilter = false; _started = false;
    }

    internal RelationshipEnumerator(IRelationshipStore store, NodeId nodeId, RelationshipId firstRelId,
        RelationshipTypeId type, Direction direction)
    {
        _store = store; _nodeId = nodeId; _currentId = firstRelId;
        _filterType = type; _direction = direction; _hasFilter = true; _started = false;
    }

    public bool MoveNext()
    {
        if (_started) _currentId = NextInChain();
        _started = true;

        while (_currentId.IsValid)
        {
            _current = _store.Read(_currentId);
            // FT-26: 論理削除された (= MVCC visibility で invisible な) record は
            // Read が InUse=false を返す。チェーンは維持されているので next に進む。
            if (!_current.InUse)
            {
                _currentId = NextInChain();
                continue;
            }
            if (!_hasFilter || Matches()) return true;
            _currentId = NextInChain();
        }
        return false;
    }

    private RelationshipId NextInChain()
    {
        // _nodeId 側のチェーンを辿る
        if (_current.Source == _nodeId)
            return _current.SourceNext;
        return _current.TargetNext;
    }

    private bool Matches()
    {
        bool typeOk = _current.Type == _filterType;
        if (!typeOk) return false;
        return _direction switch
        {
            Direction.Outgoing => _current.Source == _nodeId,
            Direction.Incoming => _current.Target == _nodeId,
            _ => true,
        };
    }

    public RelationshipReadHandle Current => _current;
    public void Dispose() { }
}

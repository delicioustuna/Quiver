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
    /// の <c>RelationshipScanExpandOperator</c> が大規模 frontier 展開で利用する経路で、
    /// ノード毎リンクリストを多数辿るより順次スキャンの方が安価なケース向け。
    /// </summary>
    IEnumerable<RelationshipId> Scan();

    /// <summary>指定 sequence の現在の Generation。範囲外なら -1。</summary>
    int CurrentGeneration(long localId) => -1;

    // リレーションシップ粒度の inline property。node (INodeStore) と同型。
    // 小さい値は rel record version へ inline 格納し get/has/set/remove を O(small) 化する。
    // inline 不可な値は false を返し、呼出側 (GraphTransaction) が overflow チェーンへ回す。
    // inline を持たない実装 (旧 RelationshipStore) は false を返して overflow に委ねる (graceful degrade)。

    /// <summary>visible 版の inline 領域から property を読む。inline に無ければ false。</summary>
    bool TryGetInlineProperty(RelationshipId relId, PropertyKeyId keyId, out PropertyValue value);

    /// <summary>visible 版の inline 領域に keyId があるか。</summary>
    bool HasInlineProperty(RelationshipId relId, PropertyKeyId keyId);

    /// <summary>inline property を set (copy-on-write)。inline 不可 / 予算超過なら false。</summary>
    bool SetInlineProperty(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value);

    /// <summary>inline property を remove (copy-on-write)。inline に無ければ false。</summary>
    bool RemoveInlineProperty(RelationshipId relId, PropertyKeyId keyId);

    /// <summary>inline + overflow チェーンを結合した property 列挙子を返す。</summary>
    PropertyEnumerator EnumerateProperties(RelationshipId relId, IPropertyStore overflowStore);
}

// RelRecord レイアウト (48 バイト):
// 0 Flags(1) 1 Source(6) 7 Target(6) 13 TypeId(2) 15 SrcPrev(6) 21 SrcNext(6) 27 TgtPrev(6) 33 TgtNext(6) 39 FirstPropId(6) 45 Pad(3)
/// <summary>
/// リレーションシップレコードを読み出したハンドル。端点 (source/target)、型、両端の双方向リンク、
/// 先頭プロパティ ID を公開する (アロケーションを避ける ref struct)。
/// </summary>
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

    /// <summary>このリレーションシップの ID。</summary>
    public RelationshipId Id => _id;
    /// <summary>MVCC 可視性の結果。false なら論理削除 / 不可視で、列挙はスキップすべき。</summary>
    public bool InUse => _inUse;
    /// <summary>始点ノード。</summary>
    public NodeId Source => _source;
    /// <summary>終点ノード。</summary>
    public NodeId Target => _target;
    /// <summary>リレーションシップ型。</summary>
    public RelationshipTypeId Type => _type;
    /// <summary>始点ノードの隣接チェーン上の前エントリ。</summary>
    public RelationshipId SourcePrev => _srcPrev;
    /// <summary>始点ノードの隣接チェーン上の次エントリ。</summary>
    public RelationshipId SourceNext => _srcNext;
    /// <summary>終点ノードの隣接チェーン上の前エントリ。</summary>
    public RelationshipId TargetPrev => _tgtPrev;
    /// <summary>終点ノードの隣接チェーン上の次エントリ。</summary>
    public RelationshipId TargetNext => _tgtNext;
    /// <summary>プロパティチェーンの先頭 ID (無しは <see cref="PropertyId.Invalid"/>)。</summary>
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

    /// <summary>ハンドルを破棄する (現状は no-op)。</summary>
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

    // オンディスク Int48 は Sequence (sentinel -1 は Sequence がそのまま返す)。
    public NodeId Source
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[1..]));
        set => RecordHelpers.WriteInt48(_rec[1..], value.Sequence);
    }
    public NodeId Target
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[7..]));
        set => RecordHelpers.WriteInt48(_rec[7..], value.Sequence);
    }
    public RelationshipTypeId Type
    {
        readonly get => new(BinaryPrimitives.ReadInt16LittleEndian(_rec[13..]));
        set => BinaryPrimitives.WriteInt16LittleEndian(_rec[13..], (short)value.Value);
    }
    public RelationshipId SourcePrev
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[15..]));
        set => RecordHelpers.WriteInt48(_rec[15..], value.Sequence);
    }
    public RelationshipId SourceNext
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[21..]));
        set => RecordHelpers.WriteInt48(_rec[21..], value.Sequence);
    }
    public RelationshipId TargetPrev
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[27..]));
        set => RecordHelpers.WriteInt48(_rec[27..], value.Sequence);
    }
    public RelationshipId TargetNext
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[33..]));
        set => RecordHelpers.WriteInt48(_rec[33..], value.Sequence);
    }
    public PropertyId FirstPropertyId
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[39..]));
        set => RecordHelpers.WriteInt48(_rec[39..], value.Sequence);
    }

    public void Dispose() => _file.UnpinDirty(_pageId, 0);
}

/// <summary>
/// あるノードの隣接リレーションシップを双方向リンクに沿って列挙する前方イテレータ。
/// 型 / 方向フィルタと MVCC 可視性スキップに対応する。
/// </summary>
public ref struct RelationshipEnumerator
{
    private readonly IRelationshipStore _store;
    private readonly INodeStore _nodes;
    private readonly NodeId _nodeId;
    private readonly RelationshipTypeId _filterType;
    private readonly Direction _direction;
    private readonly bool _hasFilter;
    private RelationshipId _currentId;
    private RelationshipReadHandle _current;
    private bool _started;

    internal RelationshipEnumerator(IRelationshipStore store, INodeStore nodes, NodeId nodeId, RelationshipId firstRelId)
    {
        _store = store; _nodes = nodes; _nodeId = nodeId; _currentId = firstRelId;
        _filterType = default; _direction = Direction.Both; _hasFilter = false; _started = false;
    }

    internal RelationshipEnumerator(IRelationshipStore store, INodeStore nodes, NodeId nodeId, RelationshipId firstRelId,
        RelationshipTypeId type, Direction direction)
    {
        _store = store; _nodes = nodes; _nodeId = nodeId; _currentId = firstRelId;
        _filterType = type; _direction = direction; _hasFilter = true; _started = false;
    }

    /// <summary>次の可視リレーションシップへ進む。見つかれば <c>true</c>、列挙完了で <c>false</c>。</summary>
    public bool MoveNext()
    {
        if (_started) _currentId = NextInChain();
        _started = true;

        while (_currentId.IsValid)
        {
            var raw = _store.Read(_currentId);
            _current = new RelationshipReadHandle(
                raw.Id,
                raw.InUse,
                Materialize(raw.Source),
                Materialize(raw.Target),
                raw.Type,
                raw.SourcePrev,
                raw.SourceNext,
                raw.TargetPrev,
                raw.TargetNext,
                raw.FirstPropertyId);
            // 論理削除された (= MVCC visibility で invisible な) record は
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
        if (_current.Source.Sequence == _nodeId.Sequence)
            return _current.SourceNext;
        return _current.TargetNext;
    }

    private NodeId Materialize(NodeId id)
    {
        if (!id.IsValid) return id;
        int generation = _nodes.CurrentGeneration(id.Sequence);
        return generation < 0 ? NodeId.Invalid : NodeId.Create(id.Sequence, generation);
    }

    private bool Matches()
    {
        bool typeOk = _current.Type == _filterType;
        if (!typeOk) return false;
        return _direction switch
        {
            Direction.Outgoing => _current.Source.Sequence == _nodeId.Sequence,
            Direction.Incoming => _current.Target.Sequence == _nodeId.Sequence,
            _ => true,
        };
    }

    /// <summary>現在指しているリレーションシップの読み取りハンドル。</summary>
    public RelationshipReadHandle Current => _current;
    /// <summary>イテレータを破棄する (現状は no-op)。</summary>
    public void Dispose() { }
}

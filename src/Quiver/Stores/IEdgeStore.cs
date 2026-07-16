using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

internal interface IEdgeStore
{
    EdgeId Create(IVertexStore vertexStore, VertexId source, VertexId target, EdgeTypeId type);
    void Delete(IVertexStore vertexStore, EdgeId edgeId);
    EdgeReadHandle Read(EdgeId edgeId);
    EdgeWriteHandle Write(EdgeId edgeId);
    EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore vertexStore);
    EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore vertexStore, EdgeTypeId type, Direction direction);
    long InUseCount { get; }

    /// <summary>
    /// 生存中のすべての <see cref="EdgeId"/> をストア順 (id 0 → hwm-1) で列挙する。
    /// の <c>EdgeScanExpandOperator</c> が大規模 frontier 展開で利用する経路で、
    /// Vertex毎リンクリストを多数辿るより順次スキャンの方が安価なケース向け。
    /// </summary>
    IEnumerable<EdgeId> Scan();

    /// <summary>指定 sequence の現在の Generation。範囲外なら -1。</summary>
    int CurrentGeneration(long localId) => -1;

    /// <summary>owner-bound property version chain の列挙子を返す。</summary>
    PropertyCursor EnumerateProperties(EdgeId edgeId, IPropertyStore overflowStore);
}

// EdgeRecord レイアウト (48 バイト):
// 0 Flags(1) 1 Source(6) 7 Target(6) 13 TypeId(2) 15 SrcPrev(6) 21 SrcNext(6) 27 TgtPrev(6) 33 TgtNext(6) 39 FirstPropertyRef(6) 45 Pad(3)
/// <summary>
/// Edgeレコードを読み出したハンドル。端点 (source/target)、型、両端の双方向リンク、
/// owner-bound property version chain の先頭参照を保持する (アロケーションを避ける ref struct)。
/// </summary>
public readonly ref struct EdgeReadHandle
{
    private readonly EdgeId _id;
    private readonly bool _inUse;
    private readonly VertexId _source;
    private readonly VertexId _target;
    private readonly EdgeTypeId _type;
    private readonly EdgeId _srcPrev;
    private readonly EdgeId _srcNext;
    private readonly EdgeId _tgtPrev;
    private readonly EdgeId _tgtNext;
    private readonly PropertyVersionRef _firstPropertyRef;

    /// <summary>このEdgeの ID。</summary>
    public EdgeId Id => _id;
    /// <summary>MVCC 可視性の結果。false なら論理削除 / 不可視で、列挙はスキップすべき。</summary>
    public bool InUse => _inUse;
    /// <summary>始点Vertex。</summary>
    public VertexId Source => _source;
    /// <summary>終点Vertex。</summary>
    public VertexId Target => _target;
    /// <summary>Edge型。</summary>
    public EdgeTypeId Type => _type;
    /// <summary>始点Vertexの隣接チェーン上の前エントリ。</summary>
    public EdgeId SourcePrev => _srcPrev;
    /// <summary>始点Vertexの隣接チェーン上の次エントリ。</summary>
    public EdgeId SourceNext => _srcNext;
    /// <summary>終点Vertexの隣接チェーン上の前エントリ。</summary>
    public EdgeId TargetPrev => _tgtPrev;
    /// <summary>終点Vertexの隣接チェーン上の次エントリ。</summary>
    public EdgeId TargetNext => _tgtNext;
    internal PropertyVersionRef FirstPropertyRef => _firstPropertyRef;

    internal EdgeReadHandle(
        EdgeId id, bool inUse, VertexId source, VertexId target, EdgeTypeId type,
        EdgeId srcPrev, EdgeId srcNext, EdgeId tgtPrev, EdgeId tgtNext,
        PropertyVersionRef firstPropertyRef)
    {
        _id = id; _inUse = inUse; _source = source; _target = target; _type = type;
        _srcPrev = srcPrev; _srcNext = srcNext; _tgtPrev = tgtPrev; _tgtNext = tgtNext;
        _firstPropertyRef = firstPropertyRef;
    }

    /// <summary>ハンドルを破棄する (現状は no-op)。</summary>
    public void Dispose() { }
}

internal ref struct EdgeWriteHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private Span<byte> _rec;

    internal EdgeWriteHandle(IPagedFile file, PageId pageId, Span<byte> rec)
    {
        _file = file; _pageId = pageId; _rec = rec;
    }

    // オンディスク Int48 は Sequence (sentinel -1 は Sequence がそのまま返す)。
    public VertexId Source
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[1..]));
        set => RecordHelpers.WriteInt48(_rec[1..], value.Sequence);
    }
    public VertexId Target
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[7..]));
        set => RecordHelpers.WriteInt48(_rec[7..], value.Sequence);
    }
    public EdgeTypeId Type
    {
        readonly get => new(BinaryPrimitives.ReadInt16LittleEndian(_rec[13..]));
        set => BinaryPrimitives.WriteInt16LittleEndian(_rec[13..], (short)value.Value);
    }
    public EdgeId SourcePrev
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[15..]));
        set => RecordHelpers.WriteInt48(_rec[15..], value.Sequence);
    }
    public EdgeId SourceNext
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[21..]));
        set => RecordHelpers.WriteInt48(_rec[21..], value.Sequence);
    }
    public EdgeId TargetPrev
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[27..]));
        set => RecordHelpers.WriteInt48(_rec[27..], value.Sequence);
    }
    public EdgeId TargetNext
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[33..]));
        set => RecordHelpers.WriteInt48(_rec[33..], value.Sequence);
    }
    public PropertyVersionRef FirstPropertyRef
    {
        readonly get => new(RecordHelpers.ReadInt48(_rec[39..]));
        set => RecordHelpers.WriteInt48(_rec[39..], value.Sequence);
    }

    public void Dispose() => _file.UnpinDirty(_pageId, 0);
}

/// <summary>
/// あるVertexの隣接Edgeを双方向リンクに沿って列挙する前方イテレータ。
/// 型 / 方向フィルタと MVCC 可視性スキップに対応する。
/// </summary>
public ref struct EdgeEnumerator
{
    private readonly IEdgeStore _store;
    private readonly IVertexStore _vertices;
    private readonly VertexId _vertexId;
    private readonly EdgeTypeId _filterType;
    private readonly Direction _direction;
    private readonly bool _hasFilter;
    private EdgeId _currentId;
    private EdgeReadHandle _current;
    private bool _started;

    internal EdgeEnumerator(IEdgeStore store, IVertexStore vertices, VertexId vertexId, EdgeId firstEdgeId)
    {
        _store = store; _vertices = vertices; _vertexId = vertexId; _currentId = firstEdgeId;
        _filterType = default; _direction = Direction.Both; _hasFilter = false; _started = false;
    }

    internal EdgeEnumerator(IEdgeStore store, IVertexStore vertices, VertexId vertexId, EdgeId firstEdgeId,
        EdgeTypeId type, Direction direction)
    {
        _store = store; _vertices = vertices; _vertexId = vertexId; _currentId = firstEdgeId;
        _filterType = type; _direction = direction; _hasFilter = true; _started = false;
    }

    /// <summary>次の可視Edgeへ進む。見つかれば <c>true</c>、列挙完了で <c>false</c>。</summary>
    public bool MoveNext()
    {
        if (_started) _currentId = NextInChain();
        _started = true;

        while (_currentId.IsValid)
        {
            var raw = _store.Read(_currentId);
            _current = new EdgeReadHandle(
                raw.Id,
                raw.InUse,
                Materialize(raw.Source),
                Materialize(raw.Target),
                raw.Type,
                raw.SourcePrev,
                raw.SourceNext,
                raw.TargetPrev,
                raw.TargetNext,
                raw.FirstPropertyRef);
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

    private EdgeId NextInChain()
    {
        // _vertexId 側のチェーンを辿る
        if (_current.Source.Sequence == _vertexId.Sequence)
            return _current.SourceNext;
        return _current.TargetNext;
    }

    private VertexId Materialize(VertexId id)
    {
        if (!id.IsValid) return id;
        int generation = _vertices.CurrentGeneration(id.Sequence);
        return generation < 0 ? VertexId.Invalid : VertexId.Create(id.Sequence, generation);
    }

    private bool Matches()
    {
        bool typeOk = _current.Type == _filterType;
        if (!typeOk) return false;
        return _direction switch
        {
            Direction.Outgoing => _current.Source.Sequence == _vertexId.Sequence,
            Direction.Incoming => _current.Target.Sequence == _vertexId.Sequence,
            _ => true,
        };
    }

    /// <summary>現在指しているEdgeの読み取りハンドル。</summary>
    public EdgeReadHandle Current => _current;
    /// <summary>イテレータを破棄する (現状は no-op)。</summary>
    public void Dispose() { }
}

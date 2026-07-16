using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// vertex と nexus を結ぶ incidence record の割当と両方向 chain 列挙の契約。
/// incidence 自身は独立した MVCC entity ではなく、可視性は参照先 nexus header に従う。
/// </summary>
internal interface IIncidenceStore
{
    /// <summary>chain ポインタを指定して新しい incidence record を割り当てる。</summary>
    IncidenceId Allocate(
        NexusId nexusId,
        VertexId vertexId,
        RoleId roleId,
        IncidenceId nextInVertex,
        IncidenceId nextInNexus);

    /// <summary>
    /// record を読む。範囲外や未使用 slot は <see cref="IncidenceReadHandle.InUse"/> が
    /// <c>false</c> のハンドルを返す。可視性判定は行わないため、呼び出し側が
    /// nexus header で判定する。
    /// </summary>
    IncidenceReadHandle Read(IncidenceId incidenceId);

    /// <summary>chain ポインタを in-place 更新するハンドルを返す。Dispose で page を dirty 解放する。</summary>
    IncidenceWriteHandle Write(IncidenceId incidenceId);

    /// <summary>
    /// slot を free chain へ戻して sequence を再利用可能にする。呼び出し側 (vacuum) は
    /// slot が全 live chain から unlink 済みかつ active transaction が無いことを保証する。
    /// </summary>
    void Free(IncidenceId incidenceId);

    /// <summary>
    /// vertex が参加する incidence を列挙する。参照先 nexus header が不可視な
    /// incidence は読み飛ばし、chain の後続は失わない。
    /// </summary>
    VertexIncidenceEnumerator EnumerateByVertex(
        VertexId vertexId,
        IVertexIncidenceHeadStore vertexHeads,
        INexusStore nexuses);

    /// <summary>
    /// nexus のメンバー incidence を作成順に列挙する。
    /// nexus header が不可視な場合は何も返さない。
    /// </summary>
    NexusIncidenceEnumerator EnumerateByNexus(
        NexusId nexusId,
        INexusStore nexuses);

    /// <summary>生存 incidence 数</summary>
    long InUseCount { get; }

    /// <summary>
    /// 採番済み sequence の排他的上限。整合性診断が free slot を含む ID 空間を
    /// <see cref="Read"/> で走査するために使う。
    /// </summary>
    long SequenceHighWaterMark => 0;
}

/// <summary>
/// incidence record の読み取りスナップショット。
/// <see cref="InUse"/> は record slot の生存フラグであり、nexus の可視性は含まない。
/// </summary>
internal readonly ref struct IncidenceReadHandle
{
    internal IncidenceReadHandle(
        IncidenceId id,
        bool inUse,
        NexusId nexusId,
        VertexId vertexId,
        RoleId roleId,
        IncidenceId nextInVertex,
        IncidenceId nextInNexus)
    {
        Id = id;
        InUse = inUse;
        NexusId = nexusId;
        VertexId = vertexId;
        RoleId = roleId;
        NextInVertex = nextInVertex;
        NextInNexus = nextInNexus;
    }

    /// <summary>incidence ID</summary>
    public IncidenceId Id { get; }

    /// <summary>record slot が使用中か</summary>
    public bool InUse { get; }

    /// <summary>所属する nexus</summary>
    public NexusId NexusId { get; }

    /// <summary>参加している vertex</summary>
    public VertexId VertexId { get; }

    /// <summary>この参加の role</summary>
    public RoleId RoleId { get; }

    /// <summary>vertex chain の後方 (Invalid = chain 終端)</summary>
    public IncidenceId NextInVertex { get; }

    /// <summary>同一 nexus 内メンバー chain の後方 (Invalid = chain 終端)</summary>
    public IncidenceId NextInNexus { get; }

    public void Dispose() { }
}

/// <summary>
/// incidence の chain ポインタを in-place 更新するハンドル。<paramref name="record"/> は
/// slot 全体 (27B) を指す。Dispose するまで page を pin したままにし、Dispose で dirty 解放する。
/// オフセットは slot レイアウト (nextInVertex = 15、nextInNexus = 21) に対応する。
/// </summary>
internal ref struct IncidenceWriteHandle
{
    private const int OffNexus = 1;
    private const int OffVertex = 7;
    private const int OffRole = 13;
    private const int OffNextInVertex = 15;
    private const int OffNextInNexus = 21;

    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private Span<byte> _record;

    internal IncidenceWriteHandle(IPagedFile file, PageId pageId, Span<byte> record)
    {
        _file = file;
        _pageId = pageId;
        _record = record;
    }

    public NexusId NexusId
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[OffNexus..]));
        set => RecordHelpers.WriteInt48(_record[OffNexus..], value.Sequence);
    }

    public VertexId VertexId
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[OffVertex..]));
        set => RecordHelpers.WriteInt48(_record[OffVertex..], value.Sequence);
    }

    public RoleId RoleId
    {
        readonly get => new(BinaryPrimitives.ReadInt16LittleEndian(_record[OffRole..]));
        set => BinaryPrimitives.WriteInt16LittleEndian(_record[OffRole..], checked((short)value.Value));
    }

    public IncidenceId NextInVertex
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[OffNextInVertex..]));
        set => RecordHelpers.WriteInt48(_record[OffNextInVertex..], value.Sequence);
    }

    public IncidenceId NextInNexus
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[OffNextInNexus..]));
        set => RecordHelpers.WriteInt48(_record[OffNextInNexus..], value.Sequence);
    }

    public void Dispose() => _file.UnpinDirty(_pageId, 0);
}

// ref struct にすることで、可視性 skip を含む chain 走査を 1 incidence あたり
// managed allocation なしで回せる (ホットパスの列挙で GC 圧を作らない)。
/// <summary>
/// vertex chain (<c>NextInVertex</c>) を辿る列挙子。
/// 参照先 nexus header が不可視な incidence は読み飛ばして次へ進む。
/// 可視な header を観測するたびに SSN read set への記録が発生する。
/// </summary>
internal ref struct VertexIncidenceEnumerator
{
    private readonly IIncidenceStore _store;
    private readonly INexusStore _nexuses;
    private IncidenceId _nextId;
    private IncidenceReadHandle _current;

    internal VertexIncidenceEnumerator(
        IIncidenceStore store,
        INexusStore nexuses,
        IncidenceId firstId)
    {
        _store = store;
        _nexuses = nexuses;
        _nextId = firstId;
        _current = default;
    }

    public bool MoveNext()
    {
        while (_nextId.IsValid)
        {
            _current = _store.Read(_nextId);
            _nextId = _current.NextInVertex;
            if (!_current.InUse)
                continue;

            using var header = _nexuses.Read(_current.NexusId);
            if (header.InUse)
                return true;
        }

        return false;
    }

    public IncidenceReadHandle Current => _current;
    public void Dispose() { }
}

// メンバー集合は作成後不変なので、可視性は chain 全体で 1 回だけ header を見れば足りる
// (vertex chain と違い incidence ごとの header 再判定は不要)。
/// <summary>
/// nexus のメンバー chain (<c>NextInNexus</c>) を作成順に辿る列挙子。
/// nexus header が不可視な場合は 1 件も返さない。
/// </summary>
internal ref struct NexusIncidenceEnumerator
{
    private readonly IIncidenceStore _store;
    private readonly INexusStore _nexuses;
    private readonly NexusId _nexusId;
    private IncidenceId _nextId;
    private IncidenceReadHandle _current;
    private bool _visibilityChecked;
    private bool _visible;

    internal NexusIncidenceEnumerator(
        IIncidenceStore store,
        INexusStore nexuses,
        NexusId nexusId,
        IncidenceId firstId)
    {
        _store = store;
        _nexuses = nexuses;
        _nexusId = nexusId;
        _nextId = firstId;
        _current = default;
        _visibilityChecked = false;
        _visible = false;
    }

    public bool MoveNext()
    {
        if (!_visibilityChecked)
        {
            using var header = _nexuses.Read(_nexusId);
            _visible = header.InUse;
            _visibilityChecked = true;
        }

        if (!_visible || !_nextId.IsValid)
            return false;

        while (_nextId.IsValid)
        {
            _current = _store.Read(_nextId);
            _nextId = _current.NextInNexus;
            if (_current.InUse)
                return true;
        }

        return false;
    }

    public IncidenceReadHandle Current => _current;
    public void Dispose() { }
}

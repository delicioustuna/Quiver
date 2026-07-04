using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// node と hyperedge を結ぶ incidence record の割当と両方向 chain 列挙の契約。
/// incidence 自身は独立した MVCC entity ではなく、可視性は参照先 hyperedge header に従う。
/// </summary>
internal interface IIncidenceStore
{
    /// <summary>chain ポインタを指定して新しい incidence record を割り当てる。</summary>
    IncidenceId Allocate(
        HyperedgeId hyperedgeId,
        NodeId nodeId,
        RoleId roleId,
        IncidenceId previousInNode,
        IncidenceId nextInNode,
        IncidenceId nextInHyperedge);

    /// <summary>
    /// record を読む。範囲外や未使用 slot は <see cref="IncidenceReadHandle.InUse"/> が
    /// <c>false</c> のハンドルを返す。可視性判定は行わないため、呼び出し側が
    /// hyperedge header で判定する。
    /// </summary>
    IncidenceReadHandle Read(IncidenceId incidenceId);

    /// <summary>chain ポインタを in-place 更新するハンドルを返す。Dispose で page を dirty 解放する。</summary>
    IncidenceWriteHandle Write(IncidenceId incidenceId);

    /// <summary>
    /// node が参加する incidence を列挙する。参照先 hyperedge header が不可視な
    /// incidence は読み飛ばし、chain の後続は失わない。
    /// </summary>
    NodeIncidenceEnumerator EnumerateByNode(
        NodeId nodeId,
        INodeIncidenceHeadStore nodeHeads,
        IHyperedgeStore hyperedges);

    /// <summary>
    /// hyperedge のメンバー incidence を作成順に列挙する。
    /// hyperedge header が不可視な場合は何も返さない。
    /// </summary>
    HyperedgeIncidenceEnumerator EnumerateByHyperedge(
        HyperedgeId hyperedgeId,
        IHyperedgeStore hyperedges);

    /// <summary>生存 incidence 数</summary>
    long InUseCount { get; }
}

/// <summary>
/// incidence record の読み取りスナップショット。
/// <see cref="InUse"/> は record slot の生存フラグであり、hyperedge の可視性は含まない。
/// </summary>
internal readonly ref struct IncidenceReadHandle
{
    internal IncidenceReadHandle(
        IncidenceId id,
        bool inUse,
        HyperedgeId hyperedgeId,
        NodeId nodeId,
        RoleId roleId,
        IncidenceId previousInNode,
        IncidenceId nextInNode,
        IncidenceId nextInHyperedge)
    {
        Id = id;
        InUse = inUse;
        HyperedgeId = hyperedgeId;
        NodeId = nodeId;
        RoleId = roleId;
        PreviousInNode = previousInNode;
        NextInNode = nextInNode;
        NextInHyperedge = nextInHyperedge;
    }

    /// <summary>incidence ID</summary>
    public IncidenceId Id { get; }

    /// <summary>record slot が使用中か</summary>
    public bool InUse { get; }

    /// <summary>所属する hyperedge</summary>
    public HyperedgeId HyperedgeId { get; }

    /// <summary>参加している node</summary>
    public NodeId NodeId { get; }

    /// <summary>この参加の role</summary>
    public RoleId RoleId { get; }

    /// <summary>node chain の前方 (Invalid = chain 先頭)</summary>
    public IncidenceId PreviousInNode { get; }

    /// <summary>node chain の後方 (Invalid = chain 終端)</summary>
    public IncidenceId NextInNode { get; }

    /// <summary>同一 hyperedge 内メンバー chain の後方 (Invalid = chain 終端)</summary>
    public IncidenceId NextInHyperedge { get; }

    public void Dispose() { }
}

/// <summary>
/// incidence の chain ポインタを in-place 更新するハンドル。
/// Dispose するまで page を pin したままにし、Dispose で dirty 解放する。
/// </summary>
internal ref struct IncidenceWriteHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private Span<byte> _record;

    internal IncidenceWriteHandle(IPagedFile file, PageId pageId, Span<byte> record)
    {
        _file = file;
        _pageId = pageId;
        _record = record;
    }

    public IncidenceId PreviousInNode
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[15..]));
        set => RecordHelpers.WriteInt48(_record[15..], value.Sequence);
    }

    public IncidenceId NextInNode
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[21..]));
        set => RecordHelpers.WriteInt48(_record[21..], value.Sequence);
    }

    public IncidenceId NextInHyperedge
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[27..]));
        set => RecordHelpers.WriteInt48(_record[27..], value.Sequence);
    }

    public void Dispose() => _file.UnpinDirty(_pageId, 0);
}

// ref struct にすることで、可視性 skip を含む chain 走査を 1 incidence あたり
// managed allocation なしで回せる (ホットパスの列挙で GC 圧を作らない)。
/// <summary>
/// node chain (<c>NextInNode</c>) を辿る列挙子。
/// 参照先 hyperedge header が不可視な incidence は読み飛ばして次へ進む。
/// 可視な header を観測するたびに SSN read set への記録が発生する。
/// </summary>
internal ref struct NodeIncidenceEnumerator
{
    private readonly IIncidenceStore _store;
    private readonly IHyperedgeStore _hyperedges;
    private IncidenceId _nextId;
    private IncidenceReadHandle _current;

    internal NodeIncidenceEnumerator(
        IIncidenceStore store,
        IHyperedgeStore hyperedges,
        IncidenceId firstId)
    {
        _store = store;
        _hyperedges = hyperedges;
        _nextId = firstId;
        _current = default;
    }

    public bool MoveNext()
    {
        while (_nextId.IsValid)
        {
            _current = _store.Read(_nextId);
            _nextId = _current.NextInNode;
            if (!_current.InUse)
                continue;

            using var header = _hyperedges.Read(_current.HyperedgeId);
            if (header.InUse)
                return true;
        }

        return false;
    }

    public IncidenceReadHandle Current => _current;
    public void Dispose() { }
}

// メンバー集合は作成後不変なので、可視性は chain 全体で 1 回だけ header を見れば足りる
// (node chain と違い incidence ごとの header 再判定は不要)。
/// <summary>
/// hyperedge のメンバー chain (<c>NextInHyperedge</c>) を作成順に辿る列挙子。
/// hyperedge header が不可視な場合は 1 件も返さない。
/// </summary>
internal ref struct HyperedgeIncidenceEnumerator
{
    private readonly IIncidenceStore _store;
    private readonly IHyperedgeStore _hyperedges;
    private readonly HyperedgeId _hyperedgeId;
    private IncidenceId _nextId;
    private IncidenceReadHandle _current;
    private bool _visibilityChecked;
    private bool _visible;

    internal HyperedgeIncidenceEnumerator(
        IIncidenceStore store,
        IHyperedgeStore hyperedges,
        HyperedgeId hyperedgeId,
        IncidenceId firstId)
    {
        _store = store;
        _hyperedges = hyperedges;
        _hyperedgeId = hyperedgeId;
        _nextId = firstId;
        _current = default;
        _visibilityChecked = false;
        _visible = false;
    }

    public bool MoveNext()
    {
        if (!_visibilityChecked)
        {
            using var header = _hyperedges.Read(_hyperedgeId);
            _visible = header.InUse;
            _visibilityChecked = true;
        }

        if (!_visible || !_nextId.IsValid)
            return false;

        while (_nextId.IsValid)
        {
            _current = _store.Read(_nextId);
            _nextId = _current.NextInHyperedge;
            if (_current.InUse)
                return true;
        }

        return false;
    }

    public IncidenceReadHandle Current => _current;
    public void Dispose() { }
}

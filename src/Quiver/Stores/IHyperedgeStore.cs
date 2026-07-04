using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>hyperedge を構成する 1 メンバー (参加 node と role の組)</summary>
internal readonly record struct IncidenceMember(NodeId NodeId, RoleId RoleId);

/// <summary>
/// MVCC 可視な hyperedge header の CRUD 契約。
/// header が可視性の正本であり、incidence は独立した可視性を持たない。
/// </summary>
internal interface IHyperedgeStore
{
    // 作成は header・incidence・node head の 3 ストアへまたがるため、書き込み順
    // (検証 → header → incidence chain → first pointer 確定) をストア側で一元管理する。
    /// <summary>
    /// 全メンバーを検証してから header と incidence chain を書き、新しい ID を返す。
    /// </summary>
    /// <param name="members">2 件以上。同じ role と node の組は 1 回だけ許可する。</param>
    /// <exception cref="ArgumentException">
    /// arity が 2 未満、無効な member、または同じ role と node の組が重複した場合。
    /// 検証はどのレコードよりも先に行い、失敗時は何も書かない。
    /// </exception>
    HyperedgeId Create(
        HyperedgeTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore incidenceStore,
        INodeIncidenceHeadStore nodeHeads);

    /// <summary>header を論理削除する。存在しない ID や削除済み ID は no-op。</summary>
    void Delete(HyperedgeId hyperedgeId);

    /// <summary>
    /// header を読む。現在のトランザクションから不可視な場合は
    /// <see cref="HyperedgeReadHandle.InUse"/> が <c>false</c> のハンドルを返す。
    /// </summary>
    HyperedgeReadHandle Read(HyperedgeId hyperedgeId);

    /// <summary>固定領域を in-place 更新するハンドルを返す。Dispose で page を dirty 解放する。</summary>
    HyperedgeWriteHandle Write(HyperedgeId hyperedgeId);

    /// <summary>可視な hyperedge の ID だけを sequence 昇順に列挙する。</summary>
    IEnumerable<HyperedgeId> Scan();

    /// <summary>生存 hyperedge 数</summary>
    long InUseCount { get; }

    /// <summary>
    /// 採番済み sequence の排他的上限。allocation-free scan が穴を含む ID 空間を
    /// <see cref="Read"/> で走査するために使う。
    /// </summary>
    long SequenceHighWaterMark => 0;

    // ===== inline property (header 15 バイト固定領域の後ろに符号化) =====

    /// <summary>inline property を読む。存在しなければ false。</summary>
    bool TryGetInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId, out PropertyValue value);

    /// <summary>inline property の存在有無を返す。</summary>
    bool HasInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId);

    /// <summary>
    /// inline property を set する。inline に収まらない (大きすぎ / 予算超過) 場合は false を返し、
    /// 呼び出し側が overflow チェーンへ回す。
    /// </summary>
    bool SetInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId, in PropertyValue value);

    /// <summary>inline property を除去する。存在しなければ false。</summary>
    bool RemoveInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId);

    /// <summary>inline property と overflow チェーンを結合して列挙する。</summary>
    PropertyEnumerator EnumerateProperties(HyperedgeId hyperedgeId, IPropertyStore overflowStore);
}

/// <summary>
/// hyperedge header の読み取りスナップショット。
/// 不可視・未使用の場合は <see cref="InUse"/> が <c>false</c> になり、他のフィールドは無効値を返す。
/// </summary>
internal readonly ref struct HyperedgeReadHandle
{
    internal HyperedgeReadHandle(
        HyperedgeId id,
        bool inUse,
        HyperedgeTypeId type,
        IncidenceId firstIncidenceId,
        PropertyId firstPropertyId,
        long xmin,
        long xmax)
    {
        Id = id;
        InUse = inUse;
        Type = type;
        FirstIncidenceId = firstIncidenceId;
        FirstPropertyId = firstPropertyId;
        Xmin = xmin;
        Xmax = xmax;
    }

    /// <summary>世代解決済みの hyperedge ID</summary>
    public HyperedgeId Id { get; }

    /// <summary>現在のトランザクションから可視な生存 record か</summary>
    public bool InUse { get; }

    /// <summary>hyperedge type</summary>
    public HyperedgeTypeId Type { get; }

    /// <summary>メンバー chain の先頭 incidence。メンバー未確定の間は Invalid。</summary>
    public IncidenceId FirstIncidenceId { get; }

    /// <summary>property overflow chain の先頭 (Invalid = property なし)</summary>
    public PropertyId FirstPropertyId { get; }

    /// <summary>record を生成したトランザクション ID</summary>
    public long Xmin { get; }

    /// <summary>record を論理削除したトランザクション ID (0 = 生存)</summary>
    public long Xmax { get; }

    public void Dispose() { }
}

/// <summary>
/// hyperedge header の 15 バイト固定領域を in-place 更新するハンドル。
/// Dispose するまで page を pin したままにし、Dispose で dirty 解放する。
/// </summary>
internal ref struct HyperedgeWriteHandle
{
    private readonly IPagedFile _file;
    private readonly PageId _pageId;
    private Span<byte> _record;

    internal HyperedgeWriteHandle(IPagedFile file, PageId pageId, Span<byte> record)
    {
        _file = file;
        _pageId = pageId;
        _record = record;
    }

    public HyperedgeTypeId Type
    {
        readonly get => new(BinaryPrimitives.ReadInt16LittleEndian(_record[1..]));
        set => BinaryPrimitives.WriteInt16LittleEndian(_record[1..], checked((short)value.Value));
    }

    public IncidenceId FirstIncidenceId
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[3..]));
        set => RecordHelpers.WriteInt48(_record[3..], value.Sequence);
    }

    public PropertyId FirstPropertyId
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[9..]));
        set => RecordHelpers.WriteInt48(_record[9..], value.Sequence);
    }

    public void Dispose() => _file.UnpinDirty(_pageId, 0);
}

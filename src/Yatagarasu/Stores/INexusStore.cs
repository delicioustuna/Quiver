using System.Buffers.Binary;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

/// <summary>nexus を構成する 1 メンバー (参加 vertex と role の組)</summary>
internal readonly record struct IncidenceMember(VertexId VertexId, RoleId RoleId);

/// <summary>
/// MVCC 可視な nexus header の CRUD 契約。
/// header が可視性の正本であり、incidence は独立した可視性を持たない。
/// </summary>
internal interface INexusStore
{
    // 作成は header・incidence・vertex head の 3 ストアへまたがるため、書き込み順
    // (検証 → header → incidence chain → first pointer 確定) をストア側で一元管理する。
    /// <summary>
    /// 全メンバーを検証してから header と incidence chain を書き、新しい ID を返す。
    /// </summary>
    /// <param name="members">2 件以上。同じ role と vertex の組は 1 回だけ許可する。</param>
    /// <exception cref="ArgumentException">
    /// arity が 2 未満、無効な member、または同じ role と vertex の組が重複した場合。
    /// 検証はどのレコードよりも先に行い、失敗時は何も書かない。
    /// </exception>
    NexusId Create(
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore incidenceStore,
        IVertexIncidenceHeadStore vertexHeads);

    /// <summary>header を論理削除する。存在しない ID や削除済み ID は no-op。</summary>
    void Delete(NexusId nexusId);

    /// <summary>
    /// header を読む。現在のトランザクションから不可視な場合は
    /// <see cref="NexusReadHandle.InUse"/> が <c>false</c> のハンドルを返す。
    /// </summary>
    NexusReadHandle Read(NexusId nexusId);

    /// <summary>固定領域を in-place 更新するハンドルを返す。Dispose で page を dirty 解放する。</summary>
    NexusWriteHandle Write(NexusId nexusId);

    /// <summary>可視な nexus の ID だけを sequence 昇順に列挙する。</summary>
    IEnumerable<NexusId> Scan();

    /// <summary>生存 nexus 数</summary>
    long InUseCount { get; }

    /// <summary>
    /// 指定 sequence の現在世代を返す。範囲外または未割り当てなら -1。
    /// 永続ベクトルなど、sequence だけを保持する参照の世代照合に使う。
    /// </summary>
    int CurrentGeneration(long sequence) => -1;

    /// <summary>
    /// 採番済み sequence の排他的上限。allocation-free scan が穴を含む ID 空間を
    /// <see cref="Read"/> で走査するために使う。
    /// </summary>
    long SequenceHighWaterMark => 0;

    /// <summary>
    /// 可視性を適用せず、割り当て済み header の物理状態を返す。
    /// 整合性診断が論理削除後か未登録参照かを区別するために使う。
    /// </summary>
    bool TryReadRawHeader(long sequence, out RawNexusHeader header)
    {
        header = default;
        return false;
    }

    /// <summary>owner-bound property version chain の列挙子を返す。</summary>
    PropertyCursor EnumerateProperties(NexusId nexusId, IPropertyStore overflowStore);
}

internal interface ITransactionNexusStore
{
    NexusId Create(
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore incidenceStore,
        IVertexIncidenceHeadStore vertexHeads,
        TransactionId transactionId);
    void Delete(NexusId nexusId, TransactionId transactionId);
    NexusReadHandle Read(NexusId nexusId, VersionVisible visibility);
    IEnumerable<NexusId> Scan(VersionVisible visibility);
}

/// <summary>
/// nexus header の読み取りスナップショット。
/// 不可視・未使用の場合は <see cref="InUse"/> が <c>false</c> になり、他のフィールドは無効値を返す。
/// </summary>
internal readonly ref struct NexusReadHandle
{
    internal NexusReadHandle(
        NexusId id,
        bool inUse,
        NexusTypeId type,
        IncidenceId firstIncidenceId,
        PropertyVersionRef firstPropertyRef,
        long xmin,
        long xmax)
    {
        Id = id;
        InUse = inUse;
        Type = type;
        FirstIncidenceId = firstIncidenceId;
        FirstPropertyRef = firstPropertyRef;
        Xmin = xmin;
        Xmax = xmax;
    }

    /// <summary>世代解決済みの nexus ID</summary>
    public NexusId Id { get; }

    /// <summary>現在のトランザクションから可視な生存 record か</summary>
    public bool InUse { get; }

    /// <summary>nexus type</summary>
    public NexusTypeId Type { get; }

    /// <summary>メンバー chain の先頭 incidence。メンバー未確定の間は Invalid。</summary>
    public IncidenceId FirstIncidenceId { get; }

    /// <summary>property overflow chain の先頭 (Invalid = property なし)</summary>
    public PropertyVersionRef FirstPropertyRef { get; }

    /// <summary>record を生成したトランザクション ID</summary>
    public long Xmin { get; }

    /// <summary>record を論理削除したトランザクション ID (0 = 生存)</summary>
    public long Xmax { get; }

    public void Dispose() { }
}

/// <summary>
/// nexus header の 15 バイト固定領域を in-place 更新するハンドル。
/// Dispose するまで page を pin したままにし、Dispose で dirty 解放する。
/// </summary>
internal ref struct NexusWriteHandle
{
    private PageWriteHandle _page;
    private Span<byte> _record;

    internal NexusWriteHandle(PageWriteHandle page, Span<byte> record)
    {
        _page = page;
        _record = record;
    }

    public NexusTypeId Type
    {
        readonly get => new(BinaryPrimitives.ReadInt16LittleEndian(_record[1..]));
        set => BinaryPrimitives.WriteInt16LittleEndian(_record[1..], checked((short)value.Value));
    }

    public IncidenceId FirstIncidenceId
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[3..]));
        set => RecordHelpers.WriteInt48(_record[3..], value.Sequence);
    }

    public PropertyVersionRef FirstPropertyRef
    {
        readonly get => new(RecordHelpers.ReadInt48(_record[9..]));
        set => RecordHelpers.WriteInt48(_record[9..], value.Sequence);
    }

    public void Dispose() => _page.Dispose();
}

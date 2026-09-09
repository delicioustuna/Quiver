using System.Buffers.Binary;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

/// <summary>
/// 27 バイト固定 slot の incidence store。vertex sequence を添字にする
/// <see cref="VertexIncidenceHeadStore"/> と同じ「sequence → page/offset 直引き」で slot を引き、
/// version チェーンも間接ポインタ層 (map / slot directory) も持たない。
/// incidence 自身は MVCC entity ではなく、可視性は参照先 nexus header に従う。
///
/// <para>slot レイアウト (27B、302 slots/page):</para>
/// <code>
///   [0]  flags           : u8    (FlagInUse)
///   [1]  nexus       : Int48 (Sequence)
///   [7]  vertex            : Int48 (Sequence)
///   [13] role            : i16
///   [15] nextInVertex      : Int48 (Sequence)
///   [21] nextInNexus : Int48 (Sequence)
/// </code>
///
/// <para>ヘッダページ (page 1) レイアウト:</para>
/// <code>
///   [0]  hwm       : i64   (採番済み slot 数 = 最大 sequence + 1)
///   [8]  freeHead  : i64   (free chain 先頭 sequence、-1 = 空)
///   [31] family    : u8    (QUIVER-SW family version sentinel)
/// </code>
/// slot 本体は page 2 以降に密配置する。<c>seq → (page = seq/302 + 2, offset = seq%302 × 27)</c>。
/// </summary>
// 直接アドレスにしたのは、可視性判定に incidence 自身の xmin/xmax を使っておらず
// (header が正本)、version ヘッダ 24B が情報として遊んでいたため。version チェーン・
// slot directory・別テナントの間接マップを全廃し、chain 1 step の間接参照を
// map lookup + slot directory の 2 段から直接アドレス 1 段へ縮めている。
// undo (abort / savepoint) と crash recovery は物理 page image でレイアウト非依存なので、
// ヒープ形式を差し替えても rollback / recovery の機構は変わらない。
internal sealed class IncidenceStore : IIncidenceStore
{
    // slot 固定領域。vertex chain からの前方リンク (旧 prevInVertex) は持たない。
    // vacuum の unlink は影響 vertex chain を head から 1 回走査する sweep で行うため
    // 逆リンクを常時維持する必要がなく、その 6B を書かないぶん WAL の member あたり実費も減る。
    // メンバー集合は不変で nexus chain は header ごと消えるため nextInNexus のみで足りる。
    private const int SlotSize = 27;
    private const int OffFlags = 0;
    private const int OffNexus = 1;
    private const int OffVertex = 7;
    private const int OffRole = 13;
    private const int OffNextInVertex = 15;
    private const int OffNextInNexus = 21;
    // 生存 slot は flags == FlagInUse。0xFF fill された未書き込み slot (flags = 0xFF) や
    // free chain に載る slot (flags = 0) と厳密に区別するため bit マスクではなく等値で判定する。
    private const byte FlagInUse = 0x01;
    private const byte FlagFree = 0x00;

    private const int HeaderFormatOffset = 31;
    private const int MetaHwm = 0;       // i64
    private const int MetaFreeHead = 8;  // i64 (-1 = 空)
    private static readonly PageId HeaderPageId = new(1);
    private static int SlotsPerPage => RecordPageMapping.PageBodySize / SlotSize; // 302

    private readonly IPagedFile _file;
    private long _hwm;
    private long _freeHead;
    private long _inUseCount;

    public IncidenceStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= HeaderPageId.Value)
        {
            _file.AllocatePage(PageKind.Header);
            _hwm = 0;
            _freeHead = -1;
            SaveMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
        }

        _inUseCount = RecomputeInUse();
    }

    public long InUseCount => _inUseCount;
    public long SequenceHighWaterMark => _hwm;

    public IncidenceId Allocate(
        NexusId nexusId,
        VertexId vertexId,
        RoleId roleId,
        IncidenceId nextInVertex,
        IncidenceId nextInNexus)
    {
        long sequence = PopFreeSlot();
        if (sequence < 0)
        {
            sequence = _hwm;
            _hwm = sequence + 1;
            SaveMeta();
        }

        var (pageId, offset) = Location(sequence);
        EnsurePage(pageId);
        using var page = _file.PinForWrite(pageId);
        Span<byte> slot = page.Data.Slice(offset, SlotSize);
        slot[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(slot[OffNexus..], nexusId.Sequence);
        RecordHelpers.WriteInt48(slot[OffVertex..], vertexId.Sequence);
        BinaryPrimitives.WriteInt16LittleEndian(slot[OffRole..], checked((short)roleId.Value));
        RecordHelpers.WriteInt48(slot[OffNextInVertex..], nextInVertex.Sequence);
        RecordHelpers.WriteInt48(slot[OffNextInNexus..], nextInNexus.Sequence);

        _inUseCount++;
        return new IncidenceId(sequence);
    }

    public IncidenceReadHandle Read(IncidenceId incidenceId)
    {
        long sequence = incidenceId.Sequence;
        if (sequence < 0 || sequence >= _hwm)
            return NotInUse(incidenceId);

        var (pageId, offset) = Location(sequence);
        if (pageId.Value >= _file.PageCount)
            return NotInUse(incidenceId);

        using var page = _file.PinForRead(pageId);
        ReadOnlySpan<byte> slot = page.Data.Slice(offset, SlotSize);
        return new IncidenceReadHandle(
            incidenceId,
            slot[OffFlags] == FlagInUse,
            new NexusId(RecordHelpers.ReadInt48(slot[OffNexus..])),
            new VertexId(RecordHelpers.ReadInt48(slot[OffVertex..])),
            new RoleId(BinaryPrimitives.ReadInt16LittleEndian(slot[OffRole..])),
            new IncidenceId(RecordHelpers.ReadInt48(slot[OffNextInVertex..])),
            new IncidenceId(RecordHelpers.ReadInt48(slot[OffNextInNexus..])));
    }

    public IncidenceWriteHandle Write(IncidenceId incidenceId)
    {
        long sequence = incidenceId.Sequence;
        var (pageId, offset) = Location(sequence);
        if (sequence < 0 || pageId.Value >= _file.PageCount)
            throw new CorruptionException($"Write on missing incidence seq={sequence}.");

        var page = _file.PinForWrite(pageId);
        try
        {
            Span<byte> fields = page.Data.Slice(offset, SlotSize);
            return new IncidenceWriteHandle(page.Transfer(), fields);
        }
        catch
        {
            page.ReleaseUnchanged();
            throw;
        }
    }

    /// <summary>
    /// slot を free chain へ戻し、再利用可能にする。呼び出し側 (vacuum) は
    /// slot が全 live chain から unlink 済みかつ active transaction が無いことを保証する。
    /// </summary>
    // 空 slot の nextInVertex フィールドを free chain のリンクに転用する。専用の free-list
    // ページを別に持たず、既存 slot 領域を再利用してヘッダ 1 語 (freeHead) だけを増やす。
    public void Free(IncidenceId incidenceId)
    {
        long sequence = incidenceId.Sequence;
        if (sequence < 0 || sequence >= _hwm)
            throw new ArgumentOutOfRangeException(nameof(incidenceId));

        var (pageId, offset) = Location(sequence);
        EnsurePage(pageId);
        using (var page = _file.PinForWrite(pageId))
        {
            Span<byte> slot = page.Data.Slice(offset, SlotSize);
            if (slot[OffFlags] == FlagInUse)
                _inUseCount--;
            slot[OffFlags] = FlagFree;
            // free chain の後続を nextInVertex に書く (-1 = chain 終端)。
            RecordHelpers.WriteInt48(slot[OffNextInVertex..], _freeHead);
        }

        _freeHead = sequence;
        SaveMeta();
    }

    public VertexIncidenceEnumerator EnumerateByVertex(
        VertexId vertexId,
        IVertexIncidenceHeadStore vertexHeads,
        INexusStore nexuses)
        => new(this, nexuses, vertexHeads.Get(vertexId));

    public NexusIncidenceEnumerator EnumerateByNexus(
        NexusId nexusId,
        INexusStore nexuses)
    {
        using var header = nexuses.Read(nexusId);
        return new NexusIncidenceEnumerator(
            this, nexuses, nexusId, header.FirstIncidenceId);
    }

    /// <summary>free chain 先頭 sequence (-1 で空)。テスト / 診断用。</summary>
    internal long FreeHead => _freeHead;

    /// <summary>採番済み slot 数 (= 最大 sequence + 1)。テスト / 診断用。</summary>
    internal long HighWaterMark => _hwm;

    internal void ReloadMeta()
    {
        LoadMeta();
        _inUseCount = RecomputeInUse();
    }

    // free chain から 1 slot 取り出す。空なら -1。取り出した slot の nextInVertex に
    // 積まれていた後続 sequence を新しい freeHead にする。
    private long PopFreeSlot()
    {
        if (_freeHead < 0)
            return -1;

        long sequence = _freeHead;
        var (pageId, offset) = Location(sequence);
        long next;
        using (var page = _file.PinForRead(pageId))
            next = RecordHelpers.ReadInt48(page.Data.Slice(offset, SlotSize)[OffNextInVertex..]);

        _freeHead = next;
        SaveMeta();
        return sequence;
    }

    private long RecomputeInUse()
    {
        long count = 0;
        for (long sequence = 0; sequence < _hwm; sequence++)
        {
            using var record = Read(new IncidenceId(sequence));
            if (record.InUse)
                count++;
        }

        return count;
    }

    private static (PageId PageId, int Offset) Location(long sequence)
        => (new PageId(sequence / SlotsPerPage + 2),
            (int)(sequence % SlotsPerPage) * SlotSize);

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
        {
            PageId allocated = _file.AllocatePage(PageKind.IncidenceRecord);
            using var page = _file.PinForWrite(allocated);
            // 全 bit 1 = 各 chain ポインタが Int48 の -1 (Invalid)、flags = 0xFF (≠ FlagInUse)。
            // 未書き込み slot を「sequence 0 を指す生存 incidence」と誤読しないための初期化。
            page.Data.Fill(byte.MaxValue);
        }
    }

    private void LoadMeta()
    {
        using var page = _file.PinForRead(HeaderPageId);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(page.Data[MetaHwm..]);
        _freeHead = BinaryPrimitives.ReadInt64LittleEndian(page.Data[MetaFreeHead..]);
    }

    private void SaveMeta(bool initialise = false)
    {
        using var page = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(page.Data[MetaHwm..], _hwm);
        BinaryPrimitives.WriteInt64LittleEndian(page.Data[MetaFreeHead..], _freeHead);
        if (initialise)
            page.Data[HeaderFormatOffset] = StorageFormatVersion.Current;
    }

    private void CheckFormatVersion()
    {
        using var page = _file.PinForRead(HeaderPageId);
        byte actual = page.Data[HeaderFormatOffset];
        if (actual != StorageFormatVersion.Current)
            throw new StorageFormatMismatchException("incidence", actual, StorageFormatVersion.Current);
    }

    private static IncidenceReadHandle NotInUse(IncidenceId id)
        => new(
            id,
            false,
            NexusId.Invalid,
            VertexId.Invalid,
            RoleId.Invalid,
            IncidenceId.Invalid,
            IncidenceId.Invalid);
}

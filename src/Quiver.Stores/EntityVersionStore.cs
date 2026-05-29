using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Stores;

/// <summary>
/// FT-31: <see cref="IEntityVersionStore"/> の PagedFile 実装。
///
/// <para>レイアウト (page = 8192B、PageHeader = 32B、body = 8160B、entry = 32B):</para>
/// <list type="bullet">
///   <item>Page 0 = PagedFile メタ (free list / page count)</item>
///   <item>Page 1 = sidecar ヘッダ (offset 31 に sidecar 専用 format version sentinel = 1)</item>
///   <item>Page 2+ = 32B × 255 entries / page。<c>localId</c> → <c>(page = localId/255 + 2, slot = localId%255)</c></item>
/// </list>
///
/// <para>本クラスは EntityKind に依存せず、Node / Relationship / Property 各 sidecar で共通利用される。
/// 3 EntityKind 分の instance を <see cref="Quiver.Wal.WalFileKind.NodeVersionMeta"/> /
/// <see cref="Quiver.Wal.WalFileKind.RelationshipVersionMeta"/> /
/// <see cref="Quiver.Wal.WalFileKind.PropertyVersionMeta"/> でそれぞれ生成する想定。</para>
///
/// <para>FT-31 時点ではこの store は backend factory から配線されていない (デッドコード相当)。
/// FT-32 で各 store の MVCC access path に紐付ける。</para>
/// </summary>
public sealed class EntityVersionStore : IEntityVersionStore
{
    /// <summary>1 エントリのサイズ (32 バイト)。</summary>
    public const int RecordSize = EntityVersionMeta.Size;

    /// <summary>1 ページに格納できるエントリ数 (255)。</summary>
    public static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFormatVersion = 31; // byte (FT-26 NodeStore と同 offset)
    internal const byte SidecarFormatVersion = 1;

    // entry 内 offset
    private const int OffsetXmin = 0;
    private const int OffsetXmax = 8;
    private const int OffsetPstamp = 16;
    private const int OffsetSstamp = 24;

    private readonly IPagedFile _file;
    private bool _disposed;

    /// <summary>
    /// 既存ファイル / 新規ファイルのいずれも受け入れる。新規時は page 0 (PagedFile メタ)
    /// に続いて page 1 を <see cref="PageKind.Header"/> として割り当て、format version を書き込む。
    /// 既存時は format version を検証する。
    /// </summary>
    public EntityVersionStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            InitHeader();
        }
        else
        {
            CheckFormatVersion();
        }
    }

    /// <inheritdoc/>
    public EntityVersionMeta Read(long localId)
    {
        if (localId < 0) return EntityVersionMeta.Unset;
        var (pageId, off) = Location(localId);
        if (pageId.Value >= _file.PageCount) return EntityVersionMeta.Unset;
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        long xmin = BinaryPrimitives.ReadInt64LittleEndian(rec[OffsetXmin..]);
        long xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[OffsetXmax..]);
        long pstamp = BinaryPrimitives.ReadInt64LittleEndian(rec[OffsetPstamp..]);
        long sstamp = BinaryPrimitives.ReadInt64LittleEndian(rec[OffsetSstamp..]);
        // 0 埋め page (= 未書き込み slot) は Unset として正規化:
        // 全フィールド 0 のとき Sstamp を long.MaxValue に翻訳する。
        if (xmin == 0 && xmax == 0 && pstamp == 0 && sstamp == 0)
            return EntityVersionMeta.Unset;
        return new EntityVersionMeta(xmin, xmax, pstamp, sstamp);
    }

    /// <inheritdoc/>
    public void Write(long localId, in EntityVersionMeta meta)
    {
        if (localId < 0)
            throw new ArgumentOutOfRangeException(nameof(localId), "LocalId は非負である必要があります。");
        var (pageId, off) = Location(localId);
        EnsurePage(pageId);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        BinaryPrimitives.WriteInt64LittleEndian(rec[OffsetXmin..], meta.Xmin);
        BinaryPrimitives.WriteInt64LittleEndian(rec[OffsetXmax..], meta.Xmax);
        BinaryPrimitives.WriteInt64LittleEndian(rec[OffsetPstamp..], meta.Pstamp);
        BinaryPrimitives.WriteInt64LittleEndian(rec[OffsetSstamp..], meta.Sstamp);
        _file.UnpinDirty(pageId, 0);
    }

    /// <inheritdoc/>
    public void UpdateXmax(long localId, long xmax) => UpdateField(localId, OffsetXmax, xmax);

    /// <inheritdoc/>
    public void UpdatePstamp(long localId, long pstamp) => UpdateField(localId, OffsetPstamp, pstamp);

    /// <inheritdoc/>
    public void UpdateSstamp(long localId, long sstamp) => UpdateField(localId, OffsetSstamp, sstamp);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }

    // --- private ---

    private void UpdateField(long localId, int fieldOffset, long value)
    {
        if (localId < 0)
            throw new ArgumentOutOfRangeException(nameof(localId), "LocalId は非負である必要があります。");
        var (pageId, off) = Location(localId);
        EnsurePage(pageId);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        BinaryPrimitives.WriteInt64LittleEndian(rec[fieldOffset..], value);
        _file.UnpinDirty(pageId, 0);
    }

    private (PageId pageId, int offset) Location(long localId)
    {
        int rpp = RecordsPerPage;
        return (new PageId(localId / rpp + 2), (int)(localId % rpp) * RecordSize);
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.NodeRecord);
    }

    private void InitHeader()
    {
        var ph = _file.PinForWrite(HeaderPageId);
        ph.Data[MetaFormatVersion] = SidecarFormatVersion;
        _file.UnpinDirty(HeaderPageId, 0);
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != SidecarFormatVersion)
            throw new FormatVersionMismatchException("entity-version-meta", v, SidecarFormatVersion);
    }
}

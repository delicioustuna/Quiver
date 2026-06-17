using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// 列化登録 (opt-in) の永続カタログ。どの <c>(EntityKind, propertyKeyId)</c>
/// が列セグメントを持つか、およびその列が使う container テナント ID を保持する。
/// 単一ヘッダページ (テナント) に packed 格納する: <c>[count:i32 | nextTenantId:i32 | … | entries]</c>。
/// entry = <c>[kind:1 | keyId:4 | tenantId:1]</c> = 6B。opt-in 用途では数〜数十件で十分なので 1 ページに収める。
/// </summary>
internal sealed class ColumnCatalog
{
    private const int EntrySize = 6;
    private const int OffEntries = 64;
    private static readonly PageId HeaderPageId = new(1);
    private const int MetaCount = 0;          // i32
    private const int MetaNextTenant = 4;     // i32
    private const int MetaFormatVersion = 31; // byte
    private const byte FirstColumnTenant = 64; // 動的列テナントの開始 ID (固定テナント 1-16 と衝突しない)

    private readonly IPagedFile _file;
    private readonly List<(EntityKind Kind, int KeyId, byte TenantId)> _entries = new();
    private int _nextTenantId;

    public ColumnCatalog(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _nextTenantId = FirstColumnTenant;
            Save();
            using var ph = _file.PinForWrite(HeaderPageId);
            ph.Data[MetaFormatVersion] = FormatVersion.Current;
        }
        else
        {
            CheckFormatVersion();
            Load();
        }
    }

    public IReadOnlyList<(EntityKind Kind, int KeyId, byte TenantId)> Entries => _entries;

    public bool TryGet(EntityKind kind, int keyId, out byte tenantId)
    {
        foreach (var e in _entries)
            if (e.Kind == kind && e.KeyId == keyId) { tenantId = e.TenantId; return true; }
        tenantId = 0;
        return false;
    }

    /// <summary>新しい列を登録し、割り当てたテナント ID を返す。既存なら既存 ID を返す。</summary>
    public byte Register(EntityKind kind, int keyId)
    {
        if (TryGet(kind, keyId, out var existing)) return existing;
        if (_nextTenantId > byte.MaxValue)
            throw new InvalidOperationException("column tenant id space exhausted");
        var tenantId = (byte)_nextTenantId++;
        _entries.Add((kind, keyId, tenantId));
        Save();
        return tenantId;
    }

    /// <summary>登録を解除する (テナント ID は再利用しない)。</summary>
    public bool Unregister(EntityKind kind, int keyId)
    {
        int idx = _entries.FindIndex(e => e.Kind == kind && e.KeyId == keyId);
        if (idx < 0) return false;
        _entries.RemoveAt(idx);
        Save();
        return true;
    }

    public void Reload() => Load();

    private void Load()
    {
        _entries.Clear();
        using var h = _file.PinForRead(HeaderPageId);
        int count = BinaryPrimitives.ReadInt32LittleEndian(h.Data[MetaCount..]);
        _nextTenantId = BinaryPrimitives.ReadInt32LittleEndian(h.Data[MetaNextTenant..]);
        if (_nextTenantId < FirstColumnTenant) _nextTenantId = FirstColumnTenant;
        int pos = OffEntries;
        for (int i = 0; i < count; i++)
        {
            var kind = (EntityKind)h.Data[pos];
            int keyId = BinaryPrimitives.ReadInt32LittleEndian(h.Data[(pos + 1)..]);
            byte tenantId = h.Data[pos + 5];
            _entries.Add((kind, keyId, tenantId));
            pos += EntrySize;
        }
    }

    private void Save()
    {
        using var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt32LittleEndian(ph.Data[MetaCount..], _entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(ph.Data[MetaNextTenant..], _nextTenantId);
        int pos = OffEntries;
        foreach (var e in _entries)
        {
            ph.Data[pos] = (byte)e.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(ph.Data[(pos + 1)..], e.KeyId);
            ph.Data[pos + 5] = e.TenantId;
            pos += EntrySize;
        }
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != FormatVersion.Current)
            throw new FormatVersionMismatchException("columncatalog", v, FormatVersion.Current);
    }
}

using System.Buffers.Binary;
using System.Text;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// 永続ベクトルインデックスのカタログ。各 <see cref="VectorIndexSpec"/> と、その index が
/// 使う container テナント (payload / HNSW) の割当を保持し、単一ヘッダページ (固定テナント) に
/// packed 格納する。再起動を跨いで index 定義と payload テナントの対応を復元するために用いる。
///
/// <para>レイアウト (page 1 = ヘッダ):</para>
/// <list type="bullet">
///   <item>offset 0: count (i32)</item>
///   <item>offset 31: format version (byte)</item>
///   <item>offset 64+: 可変長エントリ列。各エントリは
///     <c>[nameLen i32 | name utf8 | kind 1 | srcKeyId 4 | dim 4 | metric 1 |
///        providerLen i32 | provider utf8 | normLen i32(−1=null) | norm utf8 |
///        payloadTenant 1 | hnswTenant 1]</c>。</item>
/// </list>
/// opt-in 用途では index は数件なので 1 ページ (8160B) に収まる。溢れたら <see cref="StorageException"/>。
/// payload テナントは <see cref="FirstVectorTenant"/> から 2 つずつ (payload / HNSW) 採番する。
/// </summary>
internal sealed class VectorIndexCatalog
{
    private const int OffCount = 0;            // i32
    private const int OffFormatVersion = 31;   // byte
    private const int OffEntries = 64;
    private static readonly PageId HeaderPageId = new(1);

    // 動的ベクトルテナントの開始 ID。固定テナント (1-16) / 列テナント (64+) と衝突しない高位レンジ。
    // index ごとに payload (= base+2i) と HNSW (= base+2i+1) の 2 つを採番する。
    internal const byte FirstVectorTenant = 200;

    private readonly IPagedFile _file;
    private readonly List<VectorCatalogEntry> _entries = new();

    public VectorIndexCatalog(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            Save();
            using var ph = _file.PinForWrite(HeaderPageId);
            ph.Data[OffFormatVersion] = FormatVersion.Current;
        }
        else
        {
            CheckFormatVersion();
            Load();
        }
    }

    public IReadOnlyList<VectorCatalogEntry> Entries => _entries;

    public bool TryGet(string name, out VectorCatalogEntry entry)
    {
        foreach (var e in _entries)
            if (string.Equals(e.Spec.Name, name, StringComparison.Ordinal)) { entry = e; return true; }
        entry = default;
        return false;
    }

    /// <summary>新しい index を登録し、割当てたテナントを含むエントリを返す。既存名は例外。</summary>
    public VectorCatalogEntry Register(VectorIndexSpec spec)
    {
        if (TryGet(spec.Name, out _))
            throw new VectorException($"Vector index '{spec.Name}' already exists.");

        int next = FirstVectorTenant + _entries.Count * 2;
        if (next + 1 > byte.MaxValue)
            throw new InvalidOperationException("vector tenant id space exhausted");
        var entry = new VectorCatalogEntry(spec, (byte)next, (byte)(next + 1));
        _entries.Add(entry);
        Save();
        return entry;
    }

    /// <summary>登録を解除する (テナントの物理回収は後続)。見つからなければ false。</summary>
    public bool Unregister(string name)
    {
        int idx = _entries.FindIndex(e => string.Equals(e.Spec.Name, name, StringComparison.Ordinal));
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
        var body = h.Data;
        int count = BinaryPrimitives.ReadInt32LittleEndian(body[OffCount..]);
        int pos = OffEntries;
        for (int i = 0; i < count; i++)
        {
            string name = ReadString(body, ref pos)!;
            var kind = (EntityKind)body[pos++];
            int srcKeyId = BinaryPrimitives.ReadInt32LittleEndian(body[pos..]); pos += 4;
            int dim = BinaryPrimitives.ReadInt32LittleEndian(body[pos..]); pos += 4;
            var metric = (DistanceMetric)body[pos++];
            string provider = ReadString(body, ref pos)!;
            string? norm = ReadString(body, ref pos);
            byte payloadTenant = body[pos++];
            byte hnswTenant = body[pos++];
            var spec = new VectorIndexSpec(name, kind, new PropertyKeyId(srcKeyId), dim, metric, provider, norm);
            _entries.Add(new VectorCatalogEntry(spec, payloadTenant, hnswTenant));
        }
    }

    private void Save()
    {
        using var ph = _file.PinForWrite(HeaderPageId);
        var body = ph.Data;
        BinaryPrimitives.WriteInt32LittleEndian(body[OffCount..], _entries.Count);
        int pos = OffEntries;
        foreach (var e in _entries)
        {
            WriteString(body, ref pos, e.Spec.Name);
            body[pos++] = (byte)e.Spec.EntityKind;
            BinaryPrimitives.WriteInt32LittleEndian(body[pos..], e.Spec.SourcePropertyKeyId.Value); pos += 4;
            BinaryPrimitives.WriteInt32LittleEndian(body[pos..], e.Spec.Dimensions); pos += 4;
            body[pos++] = (byte)e.Spec.Metric;
            WriteString(body, ref pos, e.Spec.ProviderId);
            WriteString(body, ref pos, e.Spec.NormalizationProfile);
            body[pos++] = e.PayloadTenant;
            body[pos++] = e.HnswTenant;
            if (pos > RecordPageMapping.PageBodySize)
                throw new StorageException(
                    $"Vector index catalog overflow ({_entries.Count} indexes). Chained pages not yet implemented.");
        }
    }

    private static string? ReadString(ReadOnlySpan<byte> body, ref int pos)
    {
        int len = BinaryPrimitives.ReadInt32LittleEndian(body[pos..]); pos += 4;
        if (len < 0) return null;
        string s = Encoding.UTF8.GetString(body.Slice(pos, len));
        pos += len;
        return s;
    }

    private static void WriteString(Span<byte> body, ref int pos, string? s)
    {
        if (s is null) { BinaryPrimitives.WriteInt32LittleEndian(body[pos..], -1); pos += 4; return; }
        int len = Encoding.UTF8.GetByteCount(s);
        BinaryPrimitives.WriteInt32LittleEndian(body[pos..], len); pos += 4;
        Encoding.UTF8.GetBytes(s, body[pos..]);
        pos += len;
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[OffFormatVersion];
        if (v != FormatVersion.Current)
            throw new FormatVersionMismatchException("vectorcatalog", v, FormatVersion.Current);
    }
}

/// <summary>ベクトルカタログの 1 エントリ — spec とテナント割当。</summary>
internal readonly record struct VectorCatalogEntry(VectorIndexSpec Spec, byte PayloadTenant, byte HnswTenant);

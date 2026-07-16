using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// 1 つのベクトルインデックスの payload を container テナントへ永続化する固定次元ストア。
/// Sequence をキーに <c>[gen:u16 | present:u8 | reserved:u8 | float×dim]</c> のレコードを保持する。
/// レコードはページ本体 (<see cref="RecordPageMapping.PageBodySize"/> = 8160B) を跨いで論理連続
/// バイト列として striping され、任意次元で隙間なくパックされる。
///
/// <para>書き込みは container の単一物理 <c>PagedFile</c> に乗るため、アクティブ tx の
/// <c>WalWriteSetContext</c> 下で行えば自動的にその tx の page-WAL (committed PageImage redo) に
/// 含まれる。tx コンテキスト外の書き込みは buffer pool に乗り checkpoint / close で永続化される
/// (crash-atomic ではない — autocommit 経路は呼び出し側が tx で包む)。</para>
///
/// <para><c>gen</c> はバインド先エンティティの世代。slot 再利用で別Vertexに化けた
/// stale binding を KNN read 時に世代照合で弾くために保持する。</para>
/// </summary>
internal sealed class VectorIndexPayloadStore
{
    private const int RecHeaderSize = 4;       // gen:u16 + present:u8 + reserved:u8
    private const int OffGen = 0;              // u16
    private const int OffPresent = 2;          // u8 (1=present, 0=absent)

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaHwm = 0;             // i64 採番済み seq 数
    private const int MetaDim = 8;             // i32 次元数
    private const int MetaElementType = 12;    // byte (VectorElementType)
    private const int MetaFormatVersion = 31;  // byte

    private static int Body => RecordPageMapping.PageBodySize; // 8160

    private readonly IPagedFile _file;
    private readonly int _dim;
    private readonly int _recSize;
    private readonly VectorElementType _elementType;
    private readonly VectorPayloadCache _cache;
    private long _hwm;

    public VectorIndexPayloadStore(
        IPagedFile file,
        int dim,
        VectorElementType elementType,
        VectorPayloadCacheBudget cacheBudget)
    {
        _file = file;
        if (dim <= 0) throw new VectorException($"vector payload dim must be positive (was {dim}).");
        // レコード長は要素表現に依存する (現在は Float32 固定 = 4B/要素)。表現を追加するときは
        // ここで per-element サイズを分岐し、Set/TryGet の (de)serialize をあわせて切り替える。
        if (elementType != VectorElementType.Float32)
            throw new VectorException(
                $"vector payload supports only element type {VectorElementType.Float32} " +
                $"(was {elementType}).");
        _elementType = elementType;
        _cache = new VectorPayloadCache(dim, cacheBudget);
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _dim = dim;
            _recSize = RecHeaderSize + dim * 4;
            _hwm = 0;
            SaveMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            int storedDim;
            byte storedElementType;
            using (var h = _file.PinForRead(HeaderPageId))
            {
                _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
                storedDim = BinaryPrimitives.ReadInt32LittleEndian(h.Data[MetaDim..]);
                storedElementType = h.Data[MetaElementType];
            }
            if (storedDim != dim)
                throw new VectorException($"vector payload dim mismatch: stored {storedDim}, requested {dim}.");
            if (storedElementType != (byte)elementType)
                throw new VectorException(
                    $"vector payload element type mismatch: stored {(VectorElementType)storedElementType}, " +
                    $"requested {elementType}.");
            _dim = dim;
            _recSize = RecHeaderSize + _dim * 4;
        }
    }

    public int Dimensions => _dim;

    /// <summary>採番済み seq 数 (= 最大 seq + 1)。KNN flat scan の上限。</summary>
    public long Hwm => _hwm;

    /// <summary>seq のベクトルを (世代付きで) set / 上書きする。</summary>
    public void Set(long seq, ushort generation, ReadOnlySpan<float> vector)
    {
        if (vector.Length != _dim)
            throw new VectorException($"vector payload expects {_dim} dims, got {vector.Length}.");
        long start = seq * (long)_recSize;
        Span<byte> hdr = stackalloc byte[RecHeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[OffGen..], generation);
        hdr[OffPresent] = 1;
        hdr[3] = 0;
        WriteBytes(start, hdr);
        WriteBytes(start + RecHeaderSize, MemoryMarshal.AsBytes(vector));
        if (seq >= _hwm) { _hwm = seq + 1; SaveMeta(); }
        _cache.StorePresent(seq, generation, vector);
    }

    /// <summary>seq のベクトルを論理削除する (present=0)。hwm は縮めない。</summary>
    public void Remove(long seq)
    {
        if (seq < 0 || seq >= _hwm) return;
        long start = seq * (long)_recSize;
        Span<byte> zero = stackalloc byte[1];
        zero[0] = 0;
        WriteBytes(start + OffPresent, zero);
        _cache.StoreAbsent(seq);
    }

    /// <summary>seq のベクトルを <paramref name="dest"/> へ読み出す。未設定 / 削除済みは false。</summary>
    public bool TryGet(long seq, Span<float> dest, out ushort generation)
    {
        generation = 0;
        if (seq < 0 || seq >= _hwm || dest.Length < _dim) return false;
        var cached = _cache.TryGet(seq, dest, out generation);
        if (cached == VectorPayloadCache.Lookup.Present) return true;
        if (cached == VectorPayloadCache.Lookup.Absent) return false;

        long start = seq * (long)_recSize;
        Span<byte> hdr = stackalloc byte[RecHeaderSize];
        ReadBytes(start, hdr);
        if (hdr[OffPresent] != 1)
        {
            _cache.StoreAbsent(seq);
            return false;
        }
        generation = BinaryPrimitives.ReadUInt16LittleEndian(hdr[OffGen..]);
        ReadBytes(start + RecHeaderSize, MemoryMarshal.AsBytes(dest[.._dim]));
        _cache.StorePresent(seq, generation, dest[.._dim]);
        return true;
    }

    /// <summary>
    /// HNSW distance 計算用。cache hit は slab の span を直接返して vector 全体のコピーを除去し、
    /// miss だけ <paramref name="scratch"/> へページから読み出す。
    /// </summary>
    public bool TryGetForScoring(
        long seq,
        float[] scratch,
        out ReadOnlySpan<float> vector,
        out ushort generation)
    {
        vector = default;
        generation = 0;
        if (seq < 0 || seq >= _hwm || scratch.Length < _dim) return false;

        var cached = _cache.TryGetSpan(seq, out vector, out generation);
        if (cached == VectorPayloadCache.Lookup.Present) return true;
        if (cached == VectorPayloadCache.Lookup.Absent) return false;

        var destination = scratch.AsSpan(0, _dim);
        if (!TryGet(seq, destination, out generation)) return false;
        vector = destination;
        return true;
    }

    /// <summary>recovery 用: ヘッダから hwm を読み直す (abort の before-image undo 後)。</summary>
    public void ReloadMeta()
    {
        LoadMeta();
        _cache.Clear();
    }

    // --- private: 論理バイト配列 (page 2+ を striping) ---

    private void ReadBytes(long logicalStart, Span<byte> dest)
    {
        int copied = 0;
        while (copied < dest.Length)
        {
            long pos = logicalStart + copied;
            var pid = new PageId(pos / Body + 2);
            int intra = (int)(pos % Body);
            int n = Math.Min(Body - intra, dest.Length - copied);
            if (_file.PageCount <= pid.Value)
            {
                dest.Slice(copied, n).Clear(); // 未割当ページ = zero (absent)
            }
            else
            {
                using var h = _file.PinForRead(pid);
                h.Data.Slice(intra, n).CopyTo(dest.Slice(copied, n));
            }
            copied += n;
        }
    }

    private void WriteBytes(long logicalStart, ReadOnlySpan<byte> src)
    {
        int copied = 0;
        while (copied < src.Length)
        {
            long pos = logicalStart + copied;
            var pid = new PageId(pos / Body + 2);
            int intra = (int)(pos % Body);
            while (_file.PageCount <= pid.Value)
                _file.AllocatePage(PageKind.ItemPointerMap);
            int n = Math.Min(Body - intra, src.Length - copied);
            var ph = _file.PinForWrite(pid);
            src.Slice(copied, n).CopyTo(ph.Data.Slice(intra, n));
            _file.UnpinDirty(pid, 0);
            copied += n;
        }
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != StorageFormatVersion.Current)
            throw new StorageFormatMismatchException("vectorpayload", v, StorageFormatVersion.Current);
    }

    private void SaveMeta(bool initialise = false)
    {
        using var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        BinaryPrimitives.WriteInt32LittleEndian(ph.Data[MetaDim..], _dim);
        ph.Data[MetaElementType] = (byte)_elementType;
        if (initialise)
            ph.Data[MetaFormatVersion] = StorageFormatVersion.Current;
    }
}

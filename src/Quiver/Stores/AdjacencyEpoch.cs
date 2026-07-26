using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// 不変の base 隣接ビューの永続メタデータ: base と delta を分離する edge-id watermark、
/// 単調増加する compact epoch、および最後のビルド以降に削除された base Edgeの集合
/// (tombstone)。
///
/// 旧来は <c>adj.epoch</c> サイドカーファイルに置かれていたが、
/// 単一ファイル化のため <see cref="SingleFileContainer"/> 内の専用テナント
/// (<see cref="AdjacencyContainer.EpochTenant"/>) のページへ移した。物理ページは container の
/// 単一 WAL fileKind に乗るため、tombstone 書き込みは tx 内なら WAL に記録され recovery / abort で
/// 透過的に巻き戻る (in-memory ハッシュセットは <see cref="Reload"/> で再同期する)。
///
/// テナントレイアウト:
///   論理 page 1 (header): Magic(4) "QEPC" | Version(2) | Reserved(2) | Epoch(8) | BaseEdgeHwm(8) |
///                          TombstoneCount(4)
///   論理 page 2+        : ソート済み int64 tombstone 配列 (1 ページ 1020 件)
///
/// ハッシュセットがランタイムの真実の源。
/// </summary>
internal sealed class AdjacencyEpoch
{
    private const uint Magic = 0x43504551; // "QEPC"
    private const ushort Version = 1;
    private const int TombstonesPerPage = RecordPageMapping.PageBodySize / 8; // 1020
    private static readonly PageId HeaderPage = new(1);

    private readonly IPagedFile _file;
    private readonly object _lock = new();
    private long _epoch;
    private long _baseEdgeHwm;
    private HashSet<long> _tombstones;

    public long Epoch { get { lock (_lock) return _epoch; } }
    public long BaseEdgeHwm { get { lock (_lock) return _baseEdgeHwm; } }
    public int TombstoneCount { get { lock (_lock) return _tombstones.Count; } }

    private AdjacencyEpoch(IPagedFile file, long epoch, long baseEdgeHwm, IEnumerable<long>? tombstones)
    {
        _file = file;
        _epoch = epoch;
        _baseEdgeHwm = baseEdgeHwm;
        _tombstones = tombstones is null ? new HashSet<long>() : new HashSet<long>(tombstones);
    }

    public bool IsTombstoned(long edgeId)
    {
        lock (_lock) return _tombstones.Contains(edgeId);
    }

    /// <summary>
    /// <paramref name="edgeId"/> を削除済みとしてマークする。base 範囲外の id は no-op —
    /// delta の削除はEdgeストア自身のリンクリスト解除で吸収され、tombstone は不要。
    /// </summary>
    public void Tombstone(long edgeId)
    {
        lock (_lock)
        {
            if (edgeId >= _baseEdgeHwm) return;
            if (_tombstones.Add(edgeId))
                PersistLocked();
        }
    }

    /// <summary>
    /// compact 後にメタデータを差し替える: <see cref="Epoch"/> をインクリメント、
    /// 新しい <see cref="BaseEdgeHwm"/> を採用、tombstone をすべて破棄する。
    /// </summary>
    public void ResetAfterCompact(long newBaseEdgeHwm)
    {
        lock (_lock)
        {
            _epoch++;
            _baseEdgeHwm = newBaseEdgeHwm;
            _tombstones.Clear();
            PersistLocked();
        }
    }

    /// <summary>
    /// abort / recovery がテナントページをディスク内容へ戻した後、in-memory の
    /// epoch / baseEdgeHwm / tombstone をテナントから読み直す。<see cref="BinaryGraphStorageBackendFactory"/>
    /// の ReloadStoreMeta から呼ばれる。
    /// </summary>
    public void Reload()
    {
        lock (_lock)
        {
            var (epoch, hwm, tombs) = ReadFile(_file);
            _epoch = epoch;
            _baseEdgeHwm = hwm;
            _tombstones = tombs;
        }
    }

    /// <summary>新規 base ビュー構築時 (bulk load) に epoch=1 / 指定 hwm / tombstone 空で初期化する。</summary>
    public static AdjacencyEpoch CreateNew(IPagedFile file, long baseEdgeHwm)
    {
        var e = new AdjacencyEpoch(file, 1, baseEdgeHwm, null);
        lock (e._lock) e.PersistLocked();
        return e;
    }

    /// <summary>既存テナントから epoch メタを読み込む。header 未書込なら epoch=0 / 空で返す。</summary>
    public static AdjacencyEpoch Open(IPagedFile file)
    {
        var (epoch, hwm, tombs) = ReadFile(file);
        return new AdjacencyEpoch(file, epoch, hwm, tombs);
    }

    private static (long Epoch, long Hwm, HashSet<long> Tombstones) ReadFile(IPagedFile file)
    {
        if (file.PageCount < 2) return (0, 0, new HashSet<long>());

        long epoch, hwm;
        int count;
        var hh = file.PinForRead(HeaderPage);
        try
        {
            var body = hh.Data;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(body);
            if (magic != Magic)
                throw new InvalidDataException($"adjacency epoch: bad magic 0x{magic:X8}");
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            if (version != Version)
                throw new InvalidDataException($"adjacency epoch: unsupported version {version}");
            epoch = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
            hwm = BinaryPrimitives.ReadInt64LittleEndian(body[16..]);
            count = BinaryPrimitives.ReadInt32LittleEndian(body[24..]);
        }
        finally { hh.Dispose(); }

        var tombs = new HashSet<long>(count);
        long read = 0;
        int page = 0;
        while (read < count)
        {
            var dh = file.PinForRead(new PageId(2 + page));
            try
            {
                var body = dh.Data;
                int slots = (int)Math.Min(TombstonesPerPage, count - read);
                for (int s = 0; s < slots; s++, read++)
                    tombs.Add(BinaryPrimitives.ReadInt64LittleEndian(body[(s * 8)..]));
            }
            finally { dh.Dispose(); }
            page++;
        }
        return (epoch, hwm, tombs);
    }

    private void PersistLocked()
    {
        // ソート済み配列にしてファイル内容を決定的にする (diff / golden test が安定する)。
        long[] sorted = new long[_tombstones.Count];
        _tombstones.CopyTo(sorted);
        System.Array.Sort(sorted);

        int dataPages = (sorted.Length + TombstonesPerPage - 1) / TombstonesPerPage;
        while (_file.PageCount < 2 + dataPages) _file.AllocatePage(PageKind.Header);

        var hh = _file.PinForWrite(HeaderPage);
        try
        {
            var body = hh.Data;
            body[..28].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(body, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(body[4..], Version);
            BinaryPrimitives.WriteInt64LittleEndian(body[8..], _epoch);
            BinaryPrimitives.WriteInt64LittleEndian(body[16..], _baseEdgeHwm);
            BinaryPrimitives.WriteInt32LittleEndian(body[24..], sorted.Length);
        }
        finally { hh.Dispose(); }

        long vertex = 0;
        for (int page = 0; page < dataPages; page++)
        {
            var dh = _file.PinForWrite(new PageId(2 + page));
            try
            {
                var body = dh.Data;
                int slots = (int)Math.Min(TombstonesPerPage, sorted.Length - vertex);
                for (int s = 0; s < slots; s++, vertex++)
                    BinaryPrimitives.WriteInt64LittleEndian(body[(s * 8)..], sorted[vertex]);
            }
            finally { dh.Dispose(); }
        }
    }
}

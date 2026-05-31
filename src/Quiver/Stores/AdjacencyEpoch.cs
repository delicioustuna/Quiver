using System.Buffers.Binary;

namespace Quiver.Stores;

/// <summary>
/// PW-14 / codex_advice_3 §7.6. Persistent metadata for the immutable base
/// adjacency view: the relationship-id watermark separating base from delta,
/// a monotonic compact epoch, and the set of base relationships deleted since
/// the base was last built (tombstones). Lives alongside the adjacency files
/// as <c>adj.epoch</c>.
///
/// File layout:
///   Magic(4 "QEPC") | Version(2) | Reserved(2) | Epoch(8) | BaseRelHwm(8) |
///   TombstoneCount(4) | Reserved(4) | Tombstones[Count](8 each, sorted)
///
/// Tombstone writes go through a tmp + rename so a crash mid-write leaves
/// the previous file intact. The hashset is the source of truth at runtime.
/// </summary>
internal sealed class AdjacencyEpoch
{
    private const uint Magic = 0x43504551; // "QEPC"
    private const ushort Version = 1;
    internal const int HeaderSize = 32;

    private readonly string _path;
    private readonly object _lock = new();
    private long _epoch;
    private long _baseRelHwm;
    private readonly HashSet<long> _tombstones;

    public long Epoch { get { lock (_lock) return _epoch; } }
    public long BaseRelHwm { get { lock (_lock) return _baseRelHwm; } }
    public int TombstoneCount { get { lock (_lock) return _tombstones.Count; } }

    private AdjacencyEpoch(string path, long epoch, long baseRelHwm, IEnumerable<long>? tombstones)
    {
        _path = path;
        _epoch = epoch;
        _baseRelHwm = baseRelHwm;
        _tombstones = tombstones is null ? new HashSet<long>() : new HashSet<long>(tombstones);
    }

    public bool IsTombstoned(long relId)
    {
        lock (_lock) return _tombstones.Contains(relId);
    }

    /// <summary>
    /// Mark <paramref name="relId"/> as deleted. No-op when the id is outside
    /// the base range — delta deletes are absorbed by the relationship store's
    /// own linked-list unlink and don't need a tombstone.
    /// </summary>
    public void Tombstone(long relId)
    {
        lock (_lock)
        {
            if (relId >= _baseRelHwm) return;
            if (_tombstones.Add(relId))
                PersistLocked();
        }
    }

    /// <summary>
    /// compact 後にメタデータを差し替える: <see cref="Epoch"/> をインクリメント、
    /// 新しい <see cref="BaseRelHwm"/> を採用、tombstone をすべて破棄する。永続化はアトミックに行う。
    /// </summary>
    public void ResetAfterCompact(long newBaseRelHwm)
    {
        lock (_lock)
        {
            _epoch++;
            _baseRelHwm = newBaseRelHwm;
            _tombstones.Clear();
            PersistLocked();
        }
    }

    public static AdjacencyEpoch CreateNew(string path, long baseRelHwm)
    {
        var e = new AdjacencyEpoch(path, 1, baseRelHwm, null);
        lock (e._lock) e.PersistLocked();
        return e;
    }

    public static AdjacencyEpoch Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HeaderSize];
        fs.ReadExactly(header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != Magic)
            throw new InvalidDataException($"adj.epoch: bad magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != Version)
            throw new InvalidDataException($"adj.epoch: unsupported version {version}");
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        long hwm = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);

        var tombs = new long[count];
        if (count > 0)
        {
            var bytes = new byte[count * 8];
            fs.ReadExactly(bytes);
            for (int i = 0; i < count; i++)
                tombs[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(i * 8));
        }
        return new AdjacencyEpoch(path, epoch, hwm, tombs);
    }

    private void PersistLocked()
    {
        var tmpPath = _path + ".tmp";
        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(header[8..], _epoch);
            BinaryPrimitives.WriteInt64LittleEndian(header[16..], _baseRelHwm);
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], _tombstones.Count);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..], 0);
            fs.Write(header);

            if (_tombstones.Count > 0)
            {
                var bytes = new byte[_tombstones.Count * 8];
                int i = 0;
                // ファイル内容を決定的にするためソートする — diff / golden test が安定し、
                // 将来「tombstone をソート済みスパンへロード」する probe にも好都合。
                foreach (long t in _tombstones.OrderBy(x => x))
                {
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 8), t);
                    i++;
                }
                fs.Write(bytes);
            }
            fs.Flush();
        }
        if (File.Exists(_path)) File.Delete(_path);
        File.Move(tmpPath, _path);
    }
}

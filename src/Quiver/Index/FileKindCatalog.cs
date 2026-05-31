using Quiver.Core;

namespace Quiver.Index;

/// <summary>
/// FT-19: 索引名 → WAL fileKind バイトの永続マッピング。
///
/// データファイル (Nodes / Relationships / Properties / BlobData) は固定 enum
/// <see cref="Quiver.Wal.WalFileKind"/> の 0x01..0x04 を使うが、索引は実行時に動的に作成・破棄されるため
/// 別途 catalog で fileKind を割り当てて永続化する。
///
/// レンジ: <see cref="ReservedRangeStart"/>..<see cref="ReservedRangeEnd"/> (0x40..0xFF) =
/// 192 索引まで同時保持可能。
///
/// ファイル形式: <c>indexes/.fileKinds</c> にタブ区切り 1 行 1 索引:
/// <code>
/// indexName\tbyteValue\n
/// </code>
///
/// 単一書き手前提なので lock は最小限 (mutate 経路のみ)。
/// </summary>
internal sealed class FileKindCatalog
{
    /// <summary>索引用予約 byte の下限 (含む)。データファイル 0x01..0x04 と重ならない位置。</summary>
    public const byte ReservedRangeStart = 0x40;

    /// <summary>索引用予約 byte の上限 (含む)。</summary>
    public const byte ReservedRangeEnd   = 0xFF;

    private readonly string _path;
    private readonly Dictionary<string, byte> _nameToKind = new(StringComparer.Ordinal);
    private readonly HashSet<byte> _used = [];
    private readonly object _gate = new();

    public FileKindCatalog(string indexesDirectory)
    {
        _path = Path.Combine(indexesDirectory, ".fileKinds");
        Directory.CreateDirectory(indexesDirectory);
        Load();
    }

    /// <summary>登録済み (索引名, fileKind) ペアを列挙する。再起動時の materialize 用。</summary>
    public IEnumerable<(string Name, byte FileKind)> Entries
    {
        get
        {
            lock (_gate)
            {
                // スナップショットを返して enumeration 中の mutate を許容する
                return _nameToKind.Select(kv => (kv.Key, kv.Value)).ToArray();
            }
        }
    }

    /// <summary>
    /// 指定索引名の fileKind を返す。未登録なら予約レンジから未使用の byte を割り当て、
    /// catalog ファイルに追記して fsync する。
    /// </summary>
    /// <exception cref="IndexException">予約レンジ (192 個) を使い切ったとき。</exception>
    public byte GetOrAllocate(string indexName)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        lock (_gate)
        {
            if (_nameToKind.TryGetValue(indexName, out byte existing)) return existing;

            byte allocated = AllocateUnusedLocked();
            _nameToKind[indexName] = allocated;
            _used.Add(allocated);
            PersistLocked();
            return allocated;
        }
    }

    /// <summary>登録済みなら true と fileKind を返す。allocate はしない。</summary>
    public bool TryGet(string indexName, out byte fileKind)
    {
        lock (_gate)
        {
            return _nameToKind.TryGetValue(indexName, out fileKind);
        }
    }

    /// <summary>
    /// 指定索引名を catalog から除去し、対応 fileKind を予約レンジに返却する。
    /// 索引 drop の経路で呼ぶ。
    /// </summary>
    /// <summary>
    /// OP-4: 索引名を <paramref name="oldName"/> から <paramref name="newName"/> へ rename する。
    /// fileKind は維持されるため WAL 上の PageImage / CLR の意味は変わらない。
    /// 旧名が無ければ false、新名が既に別 fileKind に使われていれば例外。
    /// </summary>
    public bool Rename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (oldName == newName) return false;
        lock (_gate)
        {
            if (!_nameToKind.TryGetValue(oldName, out byte kind)) return false;
            if (_nameToKind.ContainsKey(newName))
                throw new InvalidOperationException(
                    $"Index '{newName}' already exists in catalog.");
            _nameToKind.Remove(oldName);
            _nameToKind[newName] = kind;
            PersistLocked();
            return true;
        }
    }

    public bool Remove(string indexName)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        lock (_gate)
        {
            if (!_nameToKind.TryGetValue(indexName, out byte k)) return false;
            _nameToKind.Remove(indexName);
            _used.Remove(k);
            PersistLocked();
            return true;
        }
    }

    private byte AllocateUnusedLocked()
    {
        // 線形に最小の未使用バイトを探す。索引数 < 192 なら十分速い。
        for (int b = ReservedRangeStart; b <= ReservedRangeEnd; b++)
        {
            byte candidate = (byte)b;
            if (!_used.Contains(candidate)) return candidate;
        }
        throw new ConstraintException(
            $"fileKind catalog の予約レンジ ({ReservedRangeStart:X2}..{ReservedRangeEnd:X2}) " +
            $"を使い切った。同時保持できる索引数は最大 {ReservedRangeEnd - ReservedRangeStart + 1} 個。");
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
                throw new CorruptionException($".fileKinds に不正な行: \"{line}\"");

            string name = line[..tab];
            string byteText = line[(tab + 1)..];
            if (!byte.TryParse(byteText, out byte kind))
                throw new CorruptionException($".fileKinds の fileKind がパース不可: \"{byteText}\" (索引 \"{name}\")");
            if (kind < ReservedRangeStart || kind > ReservedRangeEnd)
                throw new CorruptionException(
                    $".fileKinds の fileKind {kind:X2} (索引 \"{name}\") が予約レンジ外。");
            if (_used.Contains(kind))
                throw new CorruptionException(
                    $".fileKinds の fileKind {kind:X2} (索引 \"{name}\") が重複。");

            _nameToKind[name] = kind;
            _used.Add(kind);
        }
    }

    private void PersistLocked()
    {
        // atomic rewrite: tmp に全部書いて fsync → rename。
        var tmpPath = _path + ".tmp";
        using (var fs = new FileStream(
            tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sw = new StreamWriter(fs))
        {
            foreach (var kv in _nameToKind)
            {
                sw.Write(kv.Key);
                sw.Write('\t');
                sw.Write(kv.Value);
                sw.Write('\n');
            }
            sw.Flush();
            fs.Flush(flushToDisk: true);
        }
        // File.Move with overwrite (atomic on Windows / Unix)
        File.Move(tmpPath, _path, overwrite: true);
    }
}

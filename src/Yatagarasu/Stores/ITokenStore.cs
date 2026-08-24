using System.Buffers.Binary;
using System.Text;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

internal interface ITokenStore<TToken> where TToken : struct
{
    TToken GetOrCreate(ReadOnlySpan<char> name);
    bool TryGet(ReadOnlySpan<char> name, out TToken token);
    ReadOnlySpan<byte> GetNameUtf8(TToken token);
    string GetName(TToken token);
    IEnumerable<TToken> All();

    /// <summary>
    /// トークン ID を保持したまま名前を <paramref name="oldName"/> から
    /// <paramref name="newName"/> へ変更する。<paramref name="oldName"/> が未登録なら
    /// 何もせず <c>false</c> を返す (冪等)。<paramref name="newName"/> が別の ID に
    /// 既に割り当てられているときは <see cref="InvalidOperationException"/>。
    /// </summary>
    bool Rename(string oldName, string newName);
}

// -----------------------------------------------------------------------
// ファイル裏付けのトークンストアの共通基底
// トークンファイル形式: 固定長フレームの並び (4+2+N バイト、N は名前最大長)
// 各フレーム: [InUse(1)][TokenIdLE(4)][NameLenLE(2)][Name(NameLen バイト)]
// シンプルな append-only 設計で、open 時に全量をメモリへ読み込む。
// -----------------------------------------------------------------------

/// <summary>
/// トークンの永続化方式を抽象化する。<see cref="FileTokenPersistence"/> は従来の
/// <c>*.tok</c> サイドカーファイル、<see cref="PagedTokenPersistence"/> は単一ファイルコンテナの
/// テナント (<see cref="IPagedFile"/>) に格納する。
/// </summary>
internal interface ITokenPersistence : IDisposable
{
    /// <summary>永続化済みの全フレームを id 昇順で列挙する。</summary>
    IEnumerable<(int Id, byte[] Utf8, byte Flags)> Load();

    /// <summary>現在の全フレーム (<paramref name="byId"/>) を丸ごと書き戻す。</summary>
    void Persist(IReadOnlyDictionary<int, byte[]> byId, IReadOnlyDictionary<int, byte> flags);
}

internal abstract class TokenStoreBase<TToken> : ITokenStore<TToken>, IDisposable where TToken : struct
{
    private readonly ITokenPersistence _persistence;
    protected readonly Dictionary<string, TToken> _byName = new(StringComparer.Ordinal);
    protected readonly Dictionary<int, byte[]> _byId = new();
    protected readonly Dictionary<int, byte> _frameFlags = new();
    private int _nextId;

    protected TokenStoreBase(ITokenPersistence persistence)
    {
        _persistence = persistence;
        LoadFromPersistence();
    }

    /// <summary>
    /// in-memory 辞書を永続化層 (ディスク) から読み直す。トークンページがコンテナの
    /// WAL ロギング対象であり、abort の before-image 復元でディスク側はトランザクション開始前へ
    /// 戻る。その際 in-memory 辞書も戻さないと「メモリにはあるがディスクには無い」トークンが生じ、
    /// 後続 commit が再永続化をスキップして reopen 時にトークンが消える。abort 後に本メソッドを
    /// 呼んで in-memory をディスクと一致させる。
    /// </summary>
    public void Reload()
    {
        _byName.Clear();
        _byId.Clear();
        _frameFlags.Clear();
        _nextId = 0;
        LoadFromPersistence();
    }

    private void LoadFromPersistence()
    {
        foreach (var (id, utf8, flags) in _persistence.Load())
        {
            string name = Encoding.UTF8.GetString(utf8);
            _byName[name] = MakeToken(id);
            _byId[id] = utf8;
            if (flags != 0) _frameFlags[id] = flags;
            if (id >= _nextId) _nextId = id + 1;
        }
    }

    protected TokenStoreBase(string filePath) : this(new FileTokenPersistence(filePath)) { }

    /// <summary>単一ファイルコンテナのテナント上にトークンを格納する。</summary>
    protected TokenStoreBase(IPagedFile file) : this(new PagedTokenPersistence(file)) { }

    public TToken GetOrCreate(ReadOnlySpan<char> name)
    {
        string key = name.ToString();
        if (_byName.TryGetValue(key, out TToken existing))
            return existing;
        return CreateToken(key);
    }

    protected TToken CreateToken(string name, byte flags = 0)
    {
        int id = _nextId++;
        TToken token = MakeToken(id);
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        _byName[name] = token;
        _byId[id] = utf8;
        if (flags != 0) _frameFlags[id] = flags;
        _persistence.Persist(_byId, _frameFlags);
        return token;
    }

    public bool TryGet(ReadOnlySpan<char> name, out TToken token)
        => _byName.TryGetValue(name.ToString(), out token);

    public ReadOnlySpan<byte> GetNameUtf8(TToken token) =>
        _byId.TryGetValue(GetId(token), out byte[]? utf8) ? utf8 : ReadOnlySpan<byte>.Empty;

    public string GetName(TToken token) =>
        _byId.TryGetValue(GetId(token), out byte[]? utf8) ? Encoding.UTF8.GetString(utf8) : string.Empty;

    public IEnumerable<TToken> All() => _byId.Keys.Select(MakeToken);

    /// <summary>
    /// 名前 → ID マップの差し替えとファイルの全書き換えで rename を実装する。
    /// 旧名が無ければ no-op (冪等)。新名が別 ID に占有されていれば例外。トークンファイルは
    /// 通常 1KB 未満で、tx 境界外の schema rename 用途のため atomic な writefile + replace
    /// で十分整合する (open 中の他プロセスからの並行読みは FileShare.Read で許容)。
    /// </summary>
    public bool Rename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (oldName == newName) return false;

        if (!_byName.TryGetValue(oldName, out var token))
        {
            // 旧名が無ければ、新名で既に存在していれば「既に rename 済み」と解釈して true 扱いにする。
            // どちらも無いときは false を返して呼び出し側の判断に委ねる。
            return _byName.ContainsKey(newName);
        }

        if (_byName.TryGetValue(newName, out var existing))
        {
            if (GetId(existing) == GetId(token)) return true; // 既に同名
            throw new InvalidOperationException(
                $"Token '{newName}' is already assigned to a different id (existing={GetId(existing)}, requested-source={GetId(token)}).");
        }

        int id = GetId(token);
        _byName.Remove(oldName);
        _byName[newName] = token;
        _byId[id] = Encoding.UTF8.GetBytes(newName);

        _persistence.Persist(_byId, _frameFlags);
        return true;
    }

    protected abstract TToken MakeToken(int id);
    protected abstract int GetId(TToken token);

    public void Dispose() => _persistence.Dispose();
}

/// <summary>
/// 従来の <c>*.tok</c> append-only ファイル裏付け。フレーム: <c>[id:4][nameLen:2][name:N]</c>。
/// </summary>
internal sealed class FileTokenPersistence : ITokenPersistence
{
    private readonly string _filePath;

    public FileTokenPersistence(string filePath) => _filePath = filePath;

    public IEnumerable<(int Id, byte[] Utf8, byte Flags)> Load()
    {
        if (!File.Exists(_filePath)) yield break;
        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        while (fs.Position < fs.Length)
        {
            int id = reader.ReadInt32();
            int nameLen = reader.ReadUInt16();
            byte[] utf8 = reader.ReadBytes(nameLen);
            byte flags = reader.ReadByte();
            yield return (id, utf8, flags);
        }
    }

    public void Persist(IReadOnlyDictionary<int, byte[]> byId, IReadOnlyDictionary<int, byte> flags)
    {
        string dir = Path.GetDirectoryName(_filePath) ?? ".";
        string tmpPath = Path.Combine(dir, Path.GetFileName(_filePath) + ".tmp");
        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (id, utf8) in byId.OrderBy(kv => kv.Key))
            {
                writer.Write(id);
                writer.Write((ushort)utf8.Length);
                writer.Write(utf8);
                writer.Write(flags.TryGetValue(id, out var f) ? f : (byte)0);
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    public void Dispose() { }
}

/// <summary>
/// 単一ファイルコンテナのテナント (<see cref="IPagedFile"/>) 裏付け。トークン辞書は小さい
/// (通常 1 ページ) ので、変更ごとに全フレームを直列化してページ連鎖へ書き戻す (rewrite-all)。
/// レイアウト: 論理 page1 = ヘッダ <c>[frameCount:4][blobLen:8]</c>、論理 page2.. = 直列化ブロブ。
/// 物理ページなので WAL / recovery / checkpoint がそのまま効く。
/// </summary>
internal sealed class PagedTokenPersistence : ITokenPersistence
{
    private static readonly PageId HeaderPage = new(1);
    private const int HdrFrameCount = 0; // int32
    private const int HdrBlobLen = 4;    // int64
    private static int BodySize => PagedFile.BodySize;

    private readonly IPagedFile _file;

    public PagedTokenPersistence(IPagedFile file) => _file = file;

    public IEnumerable<(int Id, byte[] Utf8, byte Flags)> Load()
    {
        if (_file.PageCount <= 1) yield break; // ヘッダ未確立 = 空
        int frameCount;
        long blobLen;
        {
            using var h = _file.PinForRead(HeaderPage);
            frameCount = BinaryPrimitives.ReadInt32LittleEndian(h.Data[HdrFrameCount..]);
            blobLen = BinaryPrimitives.ReadInt64LittleEndian(h.Data[HdrBlobLen..]);
        }
        if (frameCount == 0 || blobLen == 0) yield break;

        byte[] blob = new byte[blobLen];
        int off = 0;
        long logical = 2;
        while (off < blobLen)
        {
            using var h = _file.PinForRead(new PageId(logical));
            int n = (int)Math.Min(BodySize, blobLen - off);
            h.Data[..n].CopyTo(blob.AsSpan(off));
            off += n;
            logical++;
        }

        int pos = 0;
        for (int i = 0; i < frameCount; i++)
        {
            int id = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(pos)); pos += 4;
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos)); pos += 2;
            byte[] utf8 = blob.AsSpan(pos, nameLen).ToArray(); pos += nameLen;
            byte flags = blob[pos]; pos += 1;
            yield return (id, utf8, flags);
        }
    }

    public void Persist(IReadOnlyDictionary<int, byte[]> byId, IReadOnlyDictionary<int, byte> flags)
    {
        int byteLen = 0;
        foreach (var kv in byId) byteLen += 4 + 2 + kv.Value.Length + 1;
        byte[] blob = new byte[byteLen];
        int pos = 0;
        foreach (var (id, utf8) in byId.OrderBy(kv => kv.Key))
        {
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(pos), id); pos += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(pos), (ushort)utf8.Length); pos += 2;
            utf8.CopyTo(blob.AsSpan(pos)); pos += utf8.Length;
            blob[pos] = flags.TryGetValue(id, out var f) ? f : (byte)0; pos += 1;
        }

        int dataPages = byteLen == 0 ? 0 : (byteLen + BodySize - 1) / BodySize;
        EnsureLogical(1 + dataPages); // ヘッダ(logical 1) + data(logical 2..1+dataPages)

        // データページを書く
        int off = 0;
        for (int p = 0; p < dataPages; p++)
        {
            var w = _file.PinForWrite(new PageId(2 + p));
            int n = Math.Min(BodySize, byteLen - off);
            w.Data.Clear();
            blob.AsSpan(off, n).CopyTo(w.Data);
            off += n;
            _file.UnpinDirty(new PageId(2 + p), 0);
        }

        // ヘッダを書く
        var wh = _file.PinForWrite(HeaderPage);
        BinaryPrimitives.WriteInt32LittleEndian(wh.Data[HdrFrameCount..], byId.Count);
        BinaryPrimitives.WriteInt64LittleEndian(wh.Data[HdrBlobLen..], byteLen);
        _file.UnpinDirty(HeaderPage, 0);
    }

    private void EnsureLogical(long maxLogical)
    {
        while (_file.PageCount <= maxLogical)
            _file.AllocatePage(PageKind.TokenRecord);
    }

    public void Dispose() { }
}

internal sealed class LabelTokenStore : TokenStoreBase<LabelId>
{
    public LabelTokenStore(string filePath) : base(filePath) { }
    public LabelTokenStore(IPagedFile file) : base(file) { }
    protected override LabelId MakeToken(int id) => new(id);
    protected override int GetId(LabelId token) => token.Value;
}

internal sealed class EdgeTypeTokenStore : TokenStoreBase<EdgeTypeId>
{
    public EdgeTypeTokenStore(string filePath) : base(filePath) { }
    public EdgeTypeTokenStore(IPagedFile file) : base(file) { }
    protected override EdgeTypeId MakeToken(int id) => new(id);
    protected override int GetId(EdgeTypeId token) => token.Value;
}

internal sealed class PropertyKeyTokenStore : TokenStoreBase<PropertyKeyId>
{
    public PropertyKeyTokenStore(string filePath) : base(filePath) { }
    public PropertyKeyTokenStore(IPagedFile file) : base(file) { }
    protected override PropertyKeyId MakeToken(int id) => new(id);
    protected override int GetId(PropertyKeyId token) => token.Value;

    /// <summary>
    /// cardinality を指定してプロパティキーを取得または作成する。
    /// 既存キーで cardinality が一致すればそのまま返す (冪等)。不一致なら例外。
    /// </summary>
    public PropertyKeyId GetOrCreate(string name, PropertyCardinality cardinality)
    {
        if (_byName.TryGetValue(name, out PropertyKeyId existing))
        {
            var current = GetCardinality(existing);
            if (current != cardinality)
                throw new InvalidOperationException(
                    $"Property key '{name}' already exists with cardinality {current}, cannot change to {cardinality}.");
            return existing;
        }
        return CreateToken(name, (byte)cardinality);
    }

    /// <summary>指定キーの cardinality を返す。未登録キーは <see cref="PropertyCardinality.Single"/> (既定)。</summary>
    public PropertyCardinality GetCardinality(PropertyKeyId id)
        => _frameFlags.TryGetValue(id.Value, out var f) ? (PropertyCardinality)f : PropertyCardinality.Single;
}

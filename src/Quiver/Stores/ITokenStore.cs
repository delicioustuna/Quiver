using System.Text;
using Quiver.Core;

namespace Quiver.Storage.Records;

internal interface ITokenStore<TToken> where TToken : struct
{
    TToken GetOrCreate(ReadOnlySpan<char> name);
    bool TryGet(ReadOnlySpan<char> name, out TToken token);
    ReadOnlySpan<byte> GetNameUtf8(TToken token);
    string GetName(TToken token);
    IEnumerable<TToken> All();

    /// <summary>
    /// OP-4: トークン ID を保持したまま名前を <paramref name="oldName"/> から
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

internal abstract class TokenStoreBase<TToken> : ITokenStore<TToken>, IDisposable where TToken : struct
{
    private readonly string _filePath;
    protected readonly Dictionary<string, TToken> _byName = new(StringComparer.Ordinal);
    protected readonly Dictionary<int, byte[]> _byId = new();
    private int _nextId;
    private FileStream? _stream;

    protected TokenStoreBase(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public TToken GetOrCreate(ReadOnlySpan<char> name)
    {
        string key = name.ToString();
        if (_byName.TryGetValue(key, out TToken existing))
            return existing;

        int id = _nextId++;
        TToken token = MakeToken(id);
        byte[] utf8 = Encoding.UTF8.GetBytes(key);
        _byName[key] = token;
        _byId[id] = utf8;
        Append(id, utf8);
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
    /// OP-4: 名前 → ID マップの差し替えとファイルの全書き換えで rename を実装する。
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

        RewriteFile();
        return true;
    }

    private void RewriteFile()
    {
        // append-only 形式を保ったまま、現在のメモリ状態を tmp に書き出し、replace。
        // 既存 _stream を一度閉じてからファイルを差し替える。
        _stream?.Flush();
        _stream?.Dispose();
        _stream = null;

        string dir = Path.GetDirectoryName(_filePath) ?? ".";
        string tmpPath = Path.Combine(dir, Path.GetFileName(_filePath) + ".tmp");
        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (id, utf8) in _byId.OrderBy(kv => kv.Key))
            {
                writer.Write(id);
                writer.Write((ushort)utf8.Length);
                writer.Write(utf8);
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmpPath, _filePath, overwrite: true);
        _stream = new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
    }

    protected abstract TToken MakeToken(int id);
    protected abstract int GetId(TToken token);

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        _stream = new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        while (_stream.Position < _stream.Length)
        {
            int id = reader.ReadInt32();
            int nameLen = reader.ReadUInt16();
            byte[] utf8 = reader.ReadBytes(nameLen);
            string name = Encoding.UTF8.GetString(utf8);
            TToken token = MakeToken(id);
            _byName[name] = token;
            _byId[id] = utf8;
            if (id >= _nextId) _nextId = id + 1;
        }
        _stream.Seek(0, SeekOrigin.End);
    }

    public void Dispose()
    {
        _stream?.Flush();
        _stream?.Dispose();
        _stream = null;
    }

    private void Append(int id, byte[] utf8)
    {
        _stream ??= new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
        using var writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(id);                   // 32 ビット整数
        writer.Write((ushort)utf8.Length);  // 16 ビット符号なし整数
        writer.Write(utf8);
        _stream.Flush();
    }
}

internal sealed class LabelTokenStore : TokenStoreBase<LabelId>
{
    public LabelTokenStore(string filePath) : base(filePath) { }
    protected override LabelId MakeToken(int id) => new(id);
    protected override int GetId(LabelId token) => token.Value;
}

internal sealed class RelationshipTypeTokenStore : TokenStoreBase<RelationshipTypeId>
{
    public RelationshipTypeTokenStore(string filePath) : base(filePath) { }
    protected override RelationshipTypeId MakeToken(int id) => new(id);
    protected override int GetId(RelationshipTypeId token) => token.Value;
}

internal sealed class PropertyKeyTokenStore : TokenStoreBase<PropertyKeyId>
{
    public PropertyKeyTokenStore(string filePath) : base(filePath) { }
    protected override PropertyKeyId MakeToken(int id) => new(id);
    protected override int GetId(PropertyKeyId token) => token.Value;
}

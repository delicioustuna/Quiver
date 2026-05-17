using System.Text;
using Quiver.Core;

namespace Quiver.Stores;

public interface ITokenStore<TToken> where TToken : struct
{
    TToken GetOrCreate(ReadOnlySpan<char> name);
    bool TryGet(ReadOnlySpan<char> name, out TToken token);
    ReadOnlySpan<byte> GetNameUtf8(TToken token);
    string GetName(TToken token);
    IEnumerable<TToken> All();
}

// -----------------------------------------------------------------------
// ファイル裏付けのトークンストアの共通基底
// トークンファイル形式: 固定長フレームの並び (4+2+N バイト、N は名前最大長)
// 各フレーム: [InUse(1)][TokenIdLE(4)][NameLenLE(2)][Name(NameLen バイト)]
// シンプルな append-only 設計で、open 時に全量をメモリへ読み込む。
// -----------------------------------------------------------------------

public abstract class TokenStoreBase<TToken> : ITokenStore<TToken>, IDisposable where TToken : struct
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

public sealed class LabelTokenStore : TokenStoreBase<LabelId>
{
    public LabelTokenStore(string filePath) : base(filePath) { }
    protected override LabelId MakeToken(int id) => new(id);
    protected override int GetId(LabelId token) => token.Value;
}

public sealed class RelationshipTypeTokenStore : TokenStoreBase<RelationshipTypeId>
{
    public RelationshipTypeTokenStore(string filePath) : base(filePath) { }
    protected override RelationshipTypeId MakeToken(int id) => new(id);
    protected override int GetId(RelationshipTypeId token) => token.Value;
}

public sealed class PropertyKeyTokenStore : TokenStoreBase<PropertyKeyId>
{
    public PropertyKeyTokenStore(string filePath) : base(filePath) { }
    protected override PropertyKeyId MakeToken(int id) => new(id);
    protected override int GetId(PropertyKeyId token) => token.Value;
}

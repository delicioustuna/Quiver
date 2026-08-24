namespace Yatagarasu.Text;

/// <summary>
/// 全文索引カタログに記録された <see cref="ITokenizer.TokenizerId"/> から
/// <see cref="ITokenizer"/> を解決するレジストリ。
/// </summary>
/// <remarks>
/// 検索時とインデックス構築時の両方がレジストリ経由でトークナイザを解決するため、
/// 将来 <c>mixed-bigram-v2</c> を純加算的に追加しても既存インデックスの再構築は不要。
/// </remarks>
public interface ITokenizerRegistry
{
    /// <summary>ID でトークナイザを解決する。未登録の場合は例外をスローする。</summary>
    ITokenizer Resolve(string tokenizerId);

    /// <summary>ID でトークナイザを解決する。未登録なら <c>false</c>。</summary>
    bool TryResolve(string tokenizerId, out ITokenizer tokenizer);

    /// <summary><see cref="ITokenizer.TokenizerId"/> でトークナイザを登録 (または上書き) する。</summary>
    void Register(ITokenizer tokenizer);
}

/// <summary>
/// 既定の <see cref="ITokenizerRegistry"/> 実装。
/// 新規インスタンスは組み込みの <see cref="MixedBigramTokenizer"/> (<c>mixed-bigram-v1</c>)
/// を自動登録する (<c>registerDefaults: false</c> で抑制可)。
/// </summary>
public sealed class TokenizerRegistry : ITokenizerRegistry
{
    private readonly Dictionary<string, ITokenizer> _byId = new(StringComparer.Ordinal);

    /// <summary>レジストリを生成する。既定で組み込みトークナイザを事前登録する。</summary>
    public TokenizerRegistry(bool registerDefaults = true)
    {
        if (registerDefaults)
        {
            Register(new MixedBigramTokenizer(emitUnigrams: false));
            Register(new MixedBigramTokenizer(emitUnigrams: true));
        }
    }

    /// <summary>組み込みトークナイザを事前登録したレジストリを生成する。</summary>
    public static TokenizerRegistry CreateDefault() => new(registerDefaults: true);

    /// <inheritdoc/>
    public void Register(ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        _byId[tokenizer.TokenizerId] = tokenizer;
    }

    /// <inheritdoc/>
    public bool TryResolve(string tokenizerId, out ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizerId);
        return _byId.TryGetValue(tokenizerId, out tokenizer!);
    }

    /// <inheritdoc/>
    public ITokenizer Resolve(string tokenizerId)
    {
        if (TryResolve(tokenizerId, out var t)) return t;
        throw new KeyNotFoundException(
            $"No tokenizer registered for id '{tokenizerId}'. Register it before opening or " +
            "querying a full-text index built with that tokenizer.");
    }
}

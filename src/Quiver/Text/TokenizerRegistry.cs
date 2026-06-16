namespace Quiver.Text;

/// <summary>
/// Resolves an <see cref="ITokenizer"/> from the <see cref="ITokenizer.TokenizerId"/>
/// recorded in a full-text index's catalog entry. Query-time and index-time code
/// must both resolve through a registry rather than hardcoding a tokenizer, so a
/// future <c>mixed-bigram-v2</c> can be added as a pure-additive option without
/// forcing existing indexes to rebuild.
/// </summary>
public interface ITokenizerRegistry
{
    /// <summary>Resolve a tokenizer by id, throwing if none is registered.</summary>
    ITokenizer Resolve(string tokenizerId);

    /// <summary>Resolve a tokenizer by id; returns false if none is registered.</summary>
    bool TryResolve(string tokenizerId, out ITokenizer tokenizer);

    /// <summary>Register (or replace) a tokenizer under its <see cref="ITokenizer.TokenizerId"/>.</summary>
    void Register(ITokenizer tokenizer);
}

/// <summary>
/// Default <see cref="ITokenizerRegistry"/>. A fresh instance registers the
/// built-in <see cref="MixedBigramTokenizer"/> (<c>mixed-bigram-v1</c>) unless
/// constructed with <c>registerDefaults: false</c>.
/// </summary>
public sealed class TokenizerRegistry : ITokenizerRegistry
{
    private readonly Dictionary<string, ITokenizer> _byId = new(StringComparer.Ordinal);

    /// <summary>Create a registry, optionally pre-registering the built-in tokenizers.</summary>
    public TokenizerRegistry(bool registerDefaults = true)
    {
        if (registerDefaults) Register(new MixedBigramTokenizer());
    }

    /// <summary>Create a registry pre-loaded with the built-in tokenizers.</summary>
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

namespace Quiver.Text;

/// <summary>
/// Lowercases ASCII A–Z in each token. Useful for tokenizers that do not
/// apply their own case folding (the built-in <see cref="MixedBigramTokenizer"/>
/// already normalizes via NFKC + ASCII lowercase, so this filter is redundant
/// when paired with it — it exists for custom tokenizer pipelines).
/// </summary>
public sealed class LowercaseFilter : ITokenFilter
{
    /// <inheritdoc/>
    public string FilterId => "lowercase-v1";

    /// <inheritdoc/>
    public void Apply(ReadOnlySpan<char> token, ITokenSink downstream)
    {
        bool hasUpper = false;
        foreach (var c in token)
        {
            if (c is >= 'A' and <= 'Z') { hasUpper = true; break; }
        }

        if (!hasUpper)
        {
            downstream.Accept(token);
            return;
        }

        Span<char> buf = token.Length <= 128
            ? stackalloc char[token.Length]
            : new char[token.Length];
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            buf[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
        downstream.Accept(buf);
    }
}

/// <summary>
/// Drops tokens that appear in a stop-word set. Stop words are matched
/// after any upstream filters (e.g. lowercase) have been applied, so the
/// set should contain lowercased forms.
/// </summary>
public sealed class StopWordFilter : ITokenFilter
{
    private readonly HashSet<string> _stopWords;

    /// <summary>Create a stop-word filter from an explicit word set.</summary>
    public StopWordFilter(IEnumerable<string> stopWords)
    {
        ArgumentNullException.ThrowIfNull(stopWords);
        _stopWords = new HashSet<string>(stopWords, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public string FilterId => "stopwords-v1";

    /// <inheritdoc/>
    public void Apply(ReadOnlySpan<char> token, ITokenSink downstream)
    {
        if (!_stopWords.Contains(token.ToString()))
            downstream.Accept(token);
    }
}

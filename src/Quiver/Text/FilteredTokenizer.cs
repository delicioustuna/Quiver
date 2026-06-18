namespace Quiver.Text;

/// <summary>
/// Wraps an <see cref="ITokenizer"/> with an ordered chain of
/// <see cref="ITokenFilter"/> stages. The pipeline is
/// <c>Tokenizer → Filter[0] → Filter[1] → … → Sink</c>.
/// Implements <see cref="ITokenizer"/> so existing code that calls
/// <c>tokenizer.Tokenize()</c> works transparently.
/// </summary>
public sealed class FilteredTokenizer : ITokenizer
{
    private readonly ITokenizer _inner;
    private readonly ITokenFilter[] _filters;

    /// <summary>Create a filtered tokenizer.</summary>
    /// <param name="inner">Base tokenizer that produces the initial token stream.</param>
    /// <param name="filters">
    /// Ordered filter chain. Each filter receives tokens from the previous stage
    /// and forwards results to the next. An empty array means no filtering (pass-through).
    /// </param>
    public FilteredTokenizer(ITokenizer inner, params ITokenFilter[] filters)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(filters);
        _inner = inner;
        _filters = filters.Length > 0 ? [.. filters] : [];
    }

    /// <summary>
    /// Composite id: base tokenizer id plus each filter id, joined by <c>'+'</c>.
    /// Example: <c>"mixed-bigram-v1+lowercase-v1+stopwords-en-v1"</c>. Persisted in the
    /// catalog so query-time resolution rebuilds the exact same pipeline.
    /// </summary>
    public string TokenizerId
    {
        get
        {
            if (_filters.Length == 0) return _inner.TokenizerId;
            var sb = new System.Text.StringBuilder(_inner.TokenizerId);
            foreach (var f in _filters)
                sb.Append('+').Append(f.FilterId);
            return sb.ToString();
        }
    }

    /// <inheritdoc/>
    public void Tokenize(ReadOnlySpan<char> text, ITokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_filters.Length == 0)
        {
            _inner.Tokenize(text, sink);
            return;
        }

        var chain = sink;
        for (int i = _filters.Length - 1; i >= 0; i--)
            chain = new FilterSinkAdapter(_filters[i], chain);
        _inner.Tokenize(text, chain);
    }

    private sealed class FilterSinkAdapter(ITokenFilter filter, ITokenSink downstream) : ITokenSink
    {
        public void Accept(ReadOnlySpan<char> token) => filter.Apply(token, downstream);
    }
}

namespace Quiver.Text;

/// <summary>
/// <see cref="ITokenizer"/> を <see cref="ITokenFilter"/> の順序付きチェーンで包むラッパ。
/// パイプラインは <c>Tokenizer → Filter[0] → Filter[1] → … → Sink</c>。
/// <see cref="ITokenizer"/> を実装するため既存コードから透過的に利用できる。
/// </summary>
public sealed class FilteredTokenizer : ITokenizer, INormTokenCounter
{
    private readonly ITokenizer _inner;
    private readonly ITokenFilter[] _filters;

    /// <summary>フィルタ付きトークナイザを生成する。</summary>
    /// <param name="inner">初期トークンストリームを生成するベーストークナイザ。</param>
    /// <param name="filters">
    /// 順序付きフィルタチェーン。各フィルタは前段のトークンを受け取り次段へ転送する。
    /// 空配列はフィルタなし (素通し)。
    /// </param>
    public FilteredTokenizer(ITokenizer inner, params ITokenFilter[] filters)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(filters);
        _inner = inner;
        _filters = filters.Length > 0 ? [.. filters] : [];
    }

    /// <summary>
    /// ベーストークナイザ ID と各フィルタ ID を <c>'+'</c> で連結した複合 ID
    /// (例: <c>"mixed-bigram-v1+lowercase-v1+stopwords-en-v1"</c>)。
    /// カタログに永続化され、検索時に同一パイプラインが再構築される。
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

    int INormTokenCounter.CountNormTokens(ReadOnlySpan<char> text)
        => _inner is INormTokenCounter counter ? counter.CountNormTokens(text) : -1;

    private sealed class FilterSinkAdapter(ITokenFilter filter, ITokenSink downstream) : ITokenSink
    {
        public void Accept(ReadOnlySpan<char> token) => filter.Apply(token, downstream);
    }
}

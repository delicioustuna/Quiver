namespace Quiver.Text;

/// <summary>
/// 各トークンの ASCII A–Z を小文字化する。独自のケースフォールディングを行わないトークナイザ向け。
/// </summary>
/// <remarks>
/// 組み込みの <see cref="MixedBigramTokenizer"/> は NFKC + ASCII 小文字化を内蔵するため、
/// そのトークナイザと組み合わせた場合このフィルタは冗長。カスタムパイプライン用途。
/// </remarks>
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
/// ストップワードセットに含まれるトークンを除去するフィルタ。
/// </summary>
/// <remarks>
/// マッチングは上流フィルタ (例: 小文字化) 適用後に行われるため、
/// セットには小文字化済みの形を含めること。
/// </remarks>
public sealed class StopWordFilter : ITokenFilter
{
    private readonly HashSet<string> _stopWords;

    /// <summary>明示的な単語セットからストップワードフィルタを生成する。</summary>
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

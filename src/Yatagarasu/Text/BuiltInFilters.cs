namespace Yatagarasu.Text;

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

    internal IReadOnlyCollection<string> StopWords => _stopWords;

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

/// <summary>
/// 日本語の異字体を、利用者が明示した一対一の文字写像から同一字形グループへ展開するフィルタ。
/// </summary>
/// <remarks>
/// 既定の全文索引には自動適用されない。必要な索引の
/// <see cref="FullTextIndexDefinition.Filters"/> に明示して使う。
/// parameterless constructor は人名・地名で頻出する保守的な組み込み写像
/// (<c>邉/邊→辺</c>、<c>髙→高</c>、<c>濵/濱→浜</c> など) を使う。
/// corpus 固有の判断が必要な場合は写像を明示する constructor を使う。
/// 原文 property は変更せず、索引時と検索時の token へ原形、標準形、登録異字体を補足する。
/// これは類義語展開と同じ token 補足であり、BM25 の文書長はベース tokenizer の norm 数を使う。
/// </remarks>
public sealed class JapaneseOrthographicVariantFilter : ITokenFilter
{
    private static readonly KeyValuePair<char, char>[] BuiltInMappings =
    [
        new('邉', '辺'),
        new('邊', '辺'),
        new('髙', '高'),
        new('﨑', '崎'),
        new('濵', '浜'),
        new('濱', '浜'),
        new('齋', '斎'),
        new('齊', '斉'),
        new('德', '徳'),
        new('澤', '沢'),
        new('瀨', '瀬'),
        new('眞', '真'),
        new('廣', '広'),
        new('國', '国'),
        new('學', '学'),
        new('櫻', '桜'),
    ];

    private readonly Dictionary<char, char> _mappings;
    private readonly Dictionary<char, char[]> _variantsByCanonical;

    /// <summary>保守的な組み込み異字体写像を使うフィルタを生成する。</summary>
    public JapaneseOrthographicVariantFilter()
        : this(BuiltInMappings)
    {
    }

    /// <summary>索引ごとに明示した異字体写像を使うフィルタを生成する。</summary>
    /// <param name="mappings">異字体から標準字体への一対一の文字写像。</param>
    public JapaneseOrthographicVariantFilter(
        IEnumerable<KeyValuePair<char, char>> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _mappings = new Dictionary<char, char>();
        foreach ((char source, char canonical) in mappings)
        {
            if (char.IsSurrogate(source) || char.IsSurrogate(canonical))
                throw new ArgumentException(
                    "Japanese orthographic mappings must use single BMP characters.",
                    nameof(mappings));
            if (source == canonical)
                continue;
            if (_mappings.TryGetValue(source, out char existing)
                && existing != canonical)
            {
                throw new ArgumentException(
                    $"Variant character '{source}' maps to multiple canonical characters.",
                    nameof(mappings));
            }
            _mappings[source] = canonical;
        }

        foreach (char canonical in _mappings.Values)
        {
            if (_mappings.ContainsKey(canonical))
            {
                throw new ArgumentException(
                    $"Canonical character '{canonical}' cannot also be a variant source.",
                    nameof(mappings));
            }
        }
        _variantsByCanonical = _mappings
            .GroupBy(static pair => pair.Value, static pair => pair.Key)
            .ToDictionary(
                static group => group.Key,
                static group => group.Order().ToArray());
    }

    internal IReadOnlyDictionary<char, char> Mappings => _mappings;

    /// <inheritdoc/>
    public string FilterId => "japanese-orthographic-v1";

    /// <inheritdoc/>
    public void Apply(ReadOnlySpan<char> token, ITokenSink downstream)
    {
        ArgumentNullException.ThrowIfNull(downstream);
        bool hasVariantGroup = false;
        for (int i = 0; i < token.Length; i++)
        {
            char canonical = _mappings.GetValueOrDefault(token[i], token[i]);
            if (_variantsByCanonical.ContainsKey(canonical))
            {
                hasVariantGroup = true;
                break;
            }
        }

        if (!hasVariantGroup)
        {
            downstream.Accept(token);
            return;
        }

        string original = token.ToString();
        var emitted = new HashSet<string>(StringComparer.Ordinal) { original };
        downstream.Accept(token);

        Span<char> canonicalBuffer = token.Length <= 128
            ? stackalloc char[token.Length]
            : new char[token.Length];
        token.CopyTo(canonicalBuffer);
        for (int i = 0; i < canonicalBuffer.Length; i++)
        {
            if (_mappings.TryGetValue(canonicalBuffer[i], out char canonical))
                canonicalBuffer[i] = canonical;
        }
        string canonicalText = canonicalBuffer.ToString();
        Emit(canonicalText);

        char[] expanded = canonicalText.ToCharArray();
        for (int i = 0; i < expanded.Length; i++)
        {
            char canonical = expanded[i];
            if (!_variantsByCanonical.TryGetValue(canonical, out char[]? variants))
                continue;
            foreach (char variant in variants)
            {
                expanded[i] = variant;
                Emit(new string(expanded));
            }
            expanded[i] = canonical;
        }

        void Emit(string candidate)
        {
            if (emitted.Add(candidate))
                downstream.Accept(candidate.AsSpan());
        }
    }
}

namespace Quiver.Text;

/// <summary>
/// Mixed bigram/word tokenizer (FTS-1, design 13 §5). Normalizes the input
/// (NFKC + ASCII lowercase) then segments it by Unicode script:
/// <list type="bullet">
/// <item>CJK runs (kana / kanji / hangul) are split into overlapping
/// <b>bigrams</b>; an isolated single CJK character is emitted as a unigram so
/// it remains searchable.</item>
/// <item>Latin / digit runs are emitted whole as a single <b>word</b> token,
/// delimited by any non-letter-or-digit character.</item>
/// <item>Everything else (whitespace, punctuation, symbols, emoji) acts as a
/// separator and produces no token.</item>
/// </list>
/// No morphological analysis (out of scope per design 12 §4): bigram imprecision
/// is tolerated because the vector side and RRF fusion compensate in the RAG use case.
/// </summary>
public sealed class MixedBigramTokenizer : ITokenizer
{
    /// <summary>Default tokenizer id recorded in the full-text index catalog.</summary>
    public const string DefaultTokenizerId = "mixed-bigram-v1";

    private readonly ITextNormalizer _normalizer;

    /// <summary>
    /// Create a tokenizer. The default normalizer applies NFKC + ASCII
    /// lowercase (no whitespace collapsing — segmentation handles separators).
    /// </summary>
    /// <param name="normalizer">
    /// Normalizer applied before segmentation. When null, a NFKC + ASCII
    /// lowercase normalizer is used. The same normalization must be used at
    /// index and query time, which is what <see cref="TokenizerId"/> pins down.
    /// </param>
    public MixedBigramTokenizer(ITextNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? new JapaneseAwareNormalizer
        {
            DefaultFlags = NormalizationFlags.UnicodeNFKC | NormalizationFlags.LowerCaseAscii,
        };
    }

    /// <inheritdoc/>
    public string TokenizerId => DefaultTokenizerId;

    /// <inheritdoc/>
    public void Tokenize(ReadOnlySpan<char> text, ITokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (text.IsEmpty) return;

        // Normalize first, then segment (design 13 §5: normalize then split).
        string normalized = _normalizer.Normalize(text).Text;
        ReadOnlySpan<char> s = normalized.AsSpan();
        int n = s.Length;

        int i = 0;
        while (i < n)
        {
            CharClass cls = Classify(s[i]);
            if (cls == CharClass.Other)
            {
                i++;
                continue;
            }

            // Extend the run while the script class stays the same.
            int start = i;
            i++;
            while (i < n && Classify(s[i]) == cls) i++;
            ReadOnlySpan<char> run = s.Slice(start, i - start);

            if (cls == CharClass.Word)
                sink.Accept(run);
            else
                EmitBigrams(run, sink);
        }
    }

    private static void EmitBigrams(ReadOnlySpan<char> run, ITokenSink sink)
    {
        if (run.Length == 1)
        {
            sink.Accept(run); // isolated CJK char -> unigram (still searchable)
            return;
        }

        for (int j = 0; j + 1 < run.Length; j++)
            sink.Accept(run.Slice(j, 2));
    }

    private enum CharClass : byte { Cjk, Word, Other }

    private static CharClass Classify(char c)
    {
        if (IsCjk(c)) return CharClass.Cjk;
        if (char.IsLetterOrDigit(c)) return CharClass.Word;
        return CharClass.Other;
    }

    // BMP CJK scripts. Supplementary-plane ideographs (CJK Ext B+, U+20000 and
    // up) arrive as surrogate pairs and fall through to Other; treating them as
    // CJK would require surrogate-aware bigram slicing, deferred past the MVP.
    private static bool IsCjk(char c)
    {
        int v = c;
        return
            (v >= 0x3005 && v <= 0x3007)   // 々 (iteration mark), 〆, 〇 — bind to adjacent kanji
         || (v >= 0x3040 && v <= 0x30FF)   // Hiragana + Katakana
         || (v >= 0x31F0 && v <= 0x31FF)   // Katakana phonetic extensions
         || (v >= 0x3400 && v <= 0x4DBF)   // CJK Unified Ideographs Extension A
         || (v >= 0x4E00 && v <= 0x9FFF)   // CJK Unified Ideographs
         || (v >= 0xF900 && v <= 0xFAFF)   // CJK Compatibility Ideographs
         || (v >= 0xAC00 && v <= 0xD7A3);  // Hangul syllables
    }
}

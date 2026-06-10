using System.Text;
using FsCheck.Xunit;
using Quiver.Text;

namespace Quiver.PropertyTests;

/// <summary>
/// FTS-1 (design 13 §9): property-based coverage of <see cref="MixedBigramTokenizer"/>
/// and the NFKC + ASCII-lowercase normalizer it uses.
///
/// 検証する不変量:
///   (A) 正規化冪等性: normalize(normalize(s)) == normalize(s)
///   (B) トークナイズ安定性: Tokenize(s) == Tokenize(normalize(s))
///   (C) bigram 境界: CJK 連続長 n に対しトークン数 = max(1, n-1)、各 bigram 長 2、
///       隣接 bigram は 1 文字オーバーラップし、連結で原文を復元できる
///   (D) 放出トークンは常に非空
/// </summary>
public class TokenizerProperties
{
    private static ITextNormalizer DefaultNormalizer() => new JapaneseAwareNormalizer
    {
        DefaultFlags = NormalizationFlags.UnicodeNFKC | NormalizationFlags.LowerCaseAscii,
    };

    private static List<string> Tokenize(string text)
    {
        var sink = new ListSink();
        new MixedBigramTokenizer().Tokenize(text.AsSpan(), sink);
        return sink.Tokens;
    }

    [Property(MaxTest = 500)]
    public bool Normalization_is_idempotent(string? s)
    {
        if (s is null) return true;
        var norm = DefaultNormalizer();
        string first;
        try { first = norm.Normalize(s.AsSpan()).Text; }
        catch (ArgumentException) { return true; } // lone surrogate / invalid unicode → skip
        var second = norm.Normalize(first.AsSpan()).Text;
        return first == second;
    }

    [Property(MaxTest = 300)]
    public bool Tokenize_is_stable_under_renormalization(string? s)
    {
        if (s is null) return true;
        List<string> once;
        string normalized;
        try
        {
            once = Tokenize(s);
            normalized = DefaultNormalizer().Normalize(s.AsSpan()).Text;
        }
        catch (ArgumentException) { return true; }
        return once.SequenceEqual(Tokenize(normalized));
    }

    [Property(MaxTest = 300)]
    public bool Tokens_are_never_empty(string? s)
    {
        if (s is null) return true;
        List<string> tokens;
        try { tokens = Tokenize(s); }
        catch (ArgumentException) { return true; }
        return tokens.TrueForAll(t => t.Length >= 1);
    }

    [Property(MaxTest = 300)]
    public bool Cjk_run_emits_overlapping_bigrams(int[]? codes)
    {
        if (codes is null || codes.Length == 0) return true;

        var sb = new StringBuilder(codes.Length);
        foreach (var x in codes) sb.Append(MapToCjk(x));
        string run = sb.ToString();
        int n = run.Length; // == codes.Length: every char is an NFKC-stable BMP CJK char

        var tokens = Tokenize(run);

        if (tokens.Count != Math.Max(1, n - 1)) return false;

        if (n == 1)
            return tokens[0].Length == 1 && tokens[0] == run; // isolated char → unigram

        // n >= 2: overlapping bigrams.
        for (int i = 0; i < tokens.Count; i++)
            if (tokens[i].Length != 2) return false;
        for (int i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i][1] != tokens[i + 1][0]) return false;

        var rebuilt = new StringBuilder(n);
        foreach (var t in tokens) rebuilt.Append(t[0]);
        rebuilt.Append(tokens[^1][1]);
        return rebuilt.ToString() == run;
    }

    // Map an arbitrary int onto an NFKC-stable BMP CJK code point: core Hiragana
    // (U+3042..U+3093) or common Kanji (U+4E00..U+9FAF). These ranges have no
    // compatibility decomposition, so normalization is identity and run length
    // is preserved.
    private static char MapToCjk(int x)
    {
        uint u = (uint)x;
        if (u % 4 == 0)
            return (char)(0x3042 + (int)(u % 82));   // ~25% hiragana for run variety
        return (char)(0x4E00 + (int)(u % 20912));     // kanji
    }

    private sealed class ListSink : ITokenSink
    {
        public List<string> Tokens { get; } = new();
        public void Accept(ReadOnlySpan<char> token) => Tokens.Add(token.ToString());
    }
}

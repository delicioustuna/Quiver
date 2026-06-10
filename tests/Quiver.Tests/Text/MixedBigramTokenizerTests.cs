using FluentAssertions;
using Quiver.Text;
using Xunit;

namespace Quiver.Tests.Text;

/// <summary>
/// FTS-1: tokenization rules of <see cref="MixedBigramTokenizer"/> per design 13 §5
/// (CJK -> overlapping bigrams, Latin/digit -> whole word, everything else -> separator).
/// </summary>
public sealed class MixedBigramTokenizerTests
{
    private static List<string> Tokenize(string text, ITokenizer? tokenizer = null)
    {
        var sink = new ListSink();
        (tokenizer ?? new MixedBigramTokenizer()).Tokenize(text.AsSpan(), sink);
        return sink.Tokens;
    }

    [Fact]
    public void Japanese_only_emits_overlapping_bigrams()
    {
        Tokenize("東京都").Should().Equal("東京", "京都");
        Tokenize("こんにちは").Should().Equal("こん", "んに", "にち", "ちは");
    }

    [Fact]
    public void Iteration_mark_binds_to_adjacent_kanji()
    {
        // 々 (U+3005) is classified as CJK so 佐々木 bigrams across it
        // instead of splitting into 佐 / 々 / 木.
        Tokenize("佐々木").Should().Equal("佐々", "々木");
    }

    [Fact]
    public void English_only_emits_whole_words_lowercased()
    {
        Tokenize("Hello World").Should().Equal("hello", "world");
        // letters + digits in one run stay a single word token.
        Tokenize("abc123").Should().Equal("abc123");
        // non-alphanumeric splits words.
        Tokenize("v1.2-rc").Should().Equal("v1", "2", "rc");
    }

    [Fact]
    public void Mixed_script_switches_token_kind_at_boundaries()
    {
        // "QuiverのHNSW実装" -> word, isolated-CJK unigram, word, CJK bigram.
        Tokenize("QuiverのHNSW実装").Should().Equal("quiver", "の", "hnsw", "実装");
    }

    [Fact]
    public void Single_cjk_character_emits_a_unigram()
    {
        Tokenize("猫").Should().Equal("猫");
        // isolated CJK char surrounded by separators is still a unigram.
        Tokenize("a 猫 b").Should().Equal("a", "猫", "b");
    }

    [Fact]
    public void Emoji_and_symbols_are_separators_and_produce_no_tokens()
    {
        Tokenize("🎉").Should().BeEmpty();
        Tokenize("!!! @#%").Should().BeEmpty();
        // emoji acts as a separator between word runs.
        Tokenize("foo😀bar").Should().Equal("foo", "bar");
    }

    [Fact]
    public void Empty_input_emits_nothing()
    {
        Tokenize("").Should().BeEmpty();
        Tokenize("   ").Should().BeEmpty();
    }

    [Fact]
    public void Nfkc_folds_fullwidth_and_halfwidth_forms()
    {
        // Full-width digits -> ASCII, full-width latin -> ASCII + lowercase.
        Tokenize("１２３").Should().Equal("123");
        Tokenize("ＡＢＣ").Should().Equal("abc");
        // Half-width katakana folds to full-width, then bigrams.
        Tokenize("ｶﾀｶﾅ").Should().Equal(Tokenize("カタカナ"));
    }

    [Fact]
    public void Tokenization_is_stable_under_pre_normalization()
    {
        // Tokenizing text equals tokenizing its already-normalized equivalent
        // (idempotency: NFKC + lowercase applied once or twice yields the same).
        foreach (var raw in new[] { "ＡＢＣ", "ｶﾀｶﾅ", "Hello 世界", "ＨＮＳＷ実装" })
        {
            var once = Tokenize(raw);
            var normalized = new JapaneseAwareNormalizer
            {
                DefaultFlags = NormalizationFlags.UnicodeNFKC | NormalizationFlags.LowerCaseAscii,
            }.Normalize(raw.AsSpan()).Text;
            Tokenize(normalized).Should().Equal(once);
        }
    }

    [Fact]
    public void TokenizerId_is_stable()
    {
        new MixedBigramTokenizer().TokenizerId.Should().Be("mixed-bigram-v1");
        MixedBigramTokenizer.DefaultTokenizerId.Should().Be("mixed-bigram-v1");
    }

    [Fact]
    public void Tokenize_rejects_null_sink()
    {
        var tok = new MixedBigramTokenizer();
        var act = () => tok.Tokenize("x".AsSpan(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class ListSink : ITokenSink
    {
        public List<string> Tokens { get; } = new();
        public void Accept(ReadOnlySpan<char> token) => Tokens.Add(token.ToString());
    }
}

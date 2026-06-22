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
    public void Unigram_tokenizer_id_is_stable()
    {
        new MixedBigramTokenizer(emitUnigrams: true).TokenizerId
            .Should().Be("mixed-bigram-unigram-v1");
        MixedBigramTokenizer.UnigramTokenizerId
            .Should().Be("mixed-bigram-unigram-v1");
    }

    // ---- unigram mode tests ----

    [Fact]
    public void Unigram_mode_emits_bigrams_and_unigrams_for_cjk_run()
    {
        var tok = new MixedBigramTokenizer(emitUnigrams: true);
        Tokenize("粉体", tok).Should().Equal("粉体", "粉", "体");
        Tokenize("東京都", tok).Should().Equal("東京", "京都", "東", "京", "都");
    }

    [Fact]
    public void Unigram_mode_isolated_cjk_still_single_unigram()
    {
        var tok = new MixedBigramTokenizer(emitUnigrams: true);
        Tokenize("猫", tok).Should().Equal("猫");
        Tokenize("a 猫 b", tok).Should().Equal("a", "猫", "b");
    }

    [Fact]
    public void Unigram_mode_mixed_script()
    {
        var tok = new MixedBigramTokenizer(emitUnigrams: true);
        // "アメリカの米料理" → all CJK (の is hiragana), single run of 8 chars
        Tokenize("アメリカの米料理", tok).Should().Equal(
            "アメ", "メリ", "リカ", "カの", "の米", "米料", "料理",
            "ア", "メ", "リ", "カ", "の", "米", "料", "理");
    }

    [Fact]
    public void Unigram_mode_word_tokens_unchanged()
    {
        var tok = new MixedBigramTokenizer(emitUnigrams: true);
        Tokenize("Hello World", tok).Should().Equal("hello", "world");
    }

    [Fact]
    public void Unigram_mode_single_char_search_finds_embedded_char()
    {
        var tok = new MixedBigramTokenizer(emitUnigrams: true);
        var tokens = Tokenize("粉体工学", tok);
        tokens.Should().Contain("粉");
        tokens.Should().Contain("体");
        tokens.Should().Contain("工");
        tokens.Should().Contain("学");
    }

    [Fact]
    public void Unigram_mode_norm_count_excludes_supplements()
    {
        var tok = new MixedBigramTokenizer(emitUnigrams: true);
        var counter = (INormTokenCounter)tok;
        // "粉体工学" → 3 bigrams (no supplements in norm count)
        counter.CountNormTokens("粉体工学".AsSpan()).Should().Be(3);
        // "猫" → 1 isolated unigram
        counter.CountNormTokens("猫".AsSpan()).Should().Be(1);
        // "Hello 世界" → 1 word + 1 bigram = 2
        counter.CountNormTokens("Hello 世界".AsSpan()).Should().Be(2);
        // "アメリカの米" → CJK run of 6 → 5 bigrams
        counter.CountNormTokens("アメリカの米".AsSpan()).Should().Be(5);
    }

    [Fact]
    public void Bigram_mode_norm_count_equals_total_tokens()
    {
        var tok = new MixedBigramTokenizer(emitUnigrams: false);
        var counter = (INormTokenCounter)tok;
        counter.CountNormTokens("粉体工学".AsSpan()).Should().Be(3);
        counter.CountNormTokens("Hello World".AsSpan()).Should().Be(2);
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

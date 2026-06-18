using FluentAssertions;
using Quiver.Text;
using Xunit;

namespace Quiver.Tests.Text;

public sealed class TokenFilterTests
{
    private static List<string> Tokenize(ITokenizer tokenizer, string text)
    {
        var sink = new ListSink();
        tokenizer.Tokenize(text.AsSpan(), sink);
        return sink.Tokens;
    }

    // ---- ITokenFilter contract ----

    [Fact]
    public void LowercaseFilter_lowercases_ascii()
    {
        var filter = new LowercaseFilter();
        var sink = new ListSink();
        filter.Apply("Hello".AsSpan(), sink);
        filter.Apply("WORLD".AsSpan(), sink);
        filter.Apply("already".AsSpan(), sink);
        sink.Tokens.Should().Equal("hello", "world", "already");
    }

    [Fact]
    public void LowercaseFilter_preserves_non_ascii()
    {
        var filter = new LowercaseFilter();
        var sink = new ListSink();
        filter.Apply("東京".AsSpan(), sink);
        sink.Tokens.Should().Equal("東京");
    }

    [Fact]
    public void StopWordFilter_drops_stop_words()
    {
        var filter = new StopWordFilter(["the", "a", "is"]);
        var sink = new ListSink();
        filter.Apply("the".AsSpan(), sink);
        filter.Apply("hello".AsSpan(), sink);
        filter.Apply("is".AsSpan(), sink);
        filter.Apply("world".AsSpan(), sink);
        sink.Tokens.Should().Equal("hello", "world");
    }

    [Fact]
    public void StopWordFilter_rejects_null_constructor_arg()
    {
        var act = () => new StopWordFilter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- FilteredTokenizer ----

    [Fact]
    public void FilteredTokenizer_no_filters_passes_through()
    {
        var inner = new MixedBigramTokenizer();
        var filtered = new FilteredTokenizer(inner);
        filtered.TokenizerId.Should().Be("mixed-bigram-v1");
        Tokenize(filtered, "Hello World").Should().Equal("hello", "world");
    }

    [Fact]
    public void FilteredTokenizer_single_filter()
    {
        var inner = new IdentityTokenizer("test-v1");
        var filtered = new FilteredTokenizer(inner, new LowercaseFilter());
        filtered.TokenizerId.Should().Be("test-v1+lowercase-v1");
        Tokenize(filtered, "Hello WORLD").Should().Equal("hello", "world");
    }

    [Fact]
    public void FilteredTokenizer_chained_filters_execute_in_order()
    {
        var inner = new IdentityTokenizer("test-v1");
        var filtered = new FilteredTokenizer(inner,
            new LowercaseFilter(),
            new StopWordFilter(["the", "a"]));
        filtered.TokenizerId.Should().Be("test-v1+lowercase-v1+stopwords-v1");
        Tokenize(filtered, "The quick brown fox").Should().Equal("quick", "brown", "fox");
    }

    [Fact]
    public void FilteredTokenizer_expanding_filter()
    {
        var inner = new IdentityTokenizer("test-v1");
        var synonym = new TestSynonymFilter();
        var filtered = new FilteredTokenizer(inner, synonym);
        Tokenize(filtered, "fast").Should().Equal("fast", "quick", "rapid");
    }

    [Fact]
    public void FilteredTokenizer_rejects_null_inner()
    {
        var act = () => new FilteredTokenizer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FilteredTokenizer_rejects_null_filters()
    {
        var act = () => new FilteredTokenizer(new MixedBigramTokenizer(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FilteredTokenizer_rejects_null_sink()
    {
        var tok = new FilteredTokenizer(new MixedBigramTokenizer(), new LowercaseFilter());
        var act = () => tok.Tokenize("x".AsSpan(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FilterId_constants_are_stable()
    {
        new LowercaseFilter().FilterId.Should().Be("lowercase-v1");
        new StopWordFilter([]).FilterId.Should().Be("stopwords-v1");
    }

    // ---- helpers ----

    private sealed class IdentityTokenizer(string id) : ITokenizer
    {
        public string TokenizerId => id;

        public void Tokenize(ReadOnlySpan<char> text, ITokenSink sink)
        {
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
                if (i >= text.Length) break;
                int start = i;
                while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
                sink.Accept(text[start..i]);
            }
        }
    }

    private sealed class TestSynonymFilter : ITokenFilter
    {
        public string FilterId => "test-synonym-v1";

        public void Apply(ReadOnlySpan<char> token, ITokenSink downstream)
        {
            downstream.Accept(token);
            string s = token.ToString();
            if (s == "fast")
            {
                downstream.Accept("quick".AsSpan());
                downstream.Accept("rapid".AsSpan());
            }
        }
    }

    private sealed class ListSink : ITokenSink
    {
        public List<string> Tokens { get; } = [];
        public void Accept(ReadOnlySpan<char> token) => Tokens.Add(token.ToString());
    }
}

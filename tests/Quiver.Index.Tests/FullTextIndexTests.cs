using System.Text;
using FluentAssertions;
using Quiver.Index.FullText;
using Quiver.Text;
using Xunit;

namespace Quiver.Index.Tests;

/// <summary>
/// FTS-2 (increment 1-2): postings composite-key codec, and the FullTextIndex
/// postings/norms storage + catalog persistence via IndexManager.
/// </summary>
public sealed class FullTextIndexTests : IDisposable
{
    private readonly string _dir;

    public FullTextIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---- PostingsKey codec ----

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(123456789L)]
    [InlineData(long.MaxValue)]
    public void PostingsKey_round_trips_entity_id(long entityId)
    {
        var key = PostingsKey.Encode(Encoding.UTF8.GetBytes("term"), entityId);
        PostingsKey.DecodeEntityId(key).Should().Be(entityId);
    }

    [Fact]
    public void PostingsKey_term_range_excludes_longer_prefix_terms()
    {
        var ab = PostingsKey.Encode(Encoding.UTF8.GetBytes("ab"), 5);
        var abc = PostingsKey.Encode(Encoding.UTF8.GetBytes("abc"), 5);
        var (lower, upper) = PostingsKey.TermRange(Encoding.UTF8.GetBytes("ab"));

        // "ab" entry is inside [lower, upper]; "abc" entry is outside (length prefix prevents collision).
        ab.AsSpan().SequenceCompareTo(lower).Should().BeGreaterThanOrEqualTo(0);
        ab.AsSpan().SequenceCompareTo(upper).Should().BeLessThanOrEqualTo(0);
        abc.AsSpan().SequenceCompareTo(upper).Should().BeGreaterThan(0);
    }

    // ---- FullTextIndex storage ----

    private static ITokenizer Tok => new MixedBigramTokenizer();

    [Fact]
    public void AddDocument_makes_terms_searchable_with_tf_and_doclen()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        var ft = mgr.CreateFullTextIndex("idx_text", "Doc", "body", MixedBigramTokenizer.DefaultTokenizerId);

        ft.AddDocument(1, Tok, "the quick brown fox");
        ft.AddDocument(2, Tok, "fox fox fox");

        ft.DocumentCount.Should().Be(2);
        ft.TryGetDocLength(1, out var dl1).Should().BeTrue();
        dl1.Should().Be(4);

        ft.GetPostings("quick").Should().ContainSingle().Which.Should().Be((1L, 1));
        var foxes = ft.GetPostings("fox");
        foxes.Should().BeEquivalentTo(new[] { (1L, 1), (2L, 3) });
    }

    [Fact]
    public void RemoveDocument_drops_postings_and_norm()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        var ft = mgr.CreateFullTextIndex("idx_text", "Doc", "body", MixedBigramTokenizer.DefaultTokenizerId);
        ft.AddDocument(1, Tok, "the quick brown fox");
        ft.AddDocument(2, Tok, "fox tail");

        ft.RemoveDocument(1, Tok, "the quick brown fox");

        ft.GetPostings("quick").Should().BeEmpty();
        ft.GetPostings("fox").Should().ContainSingle().Which.Should().Be((2L, 1));
        ft.TryGetDocLength(1, out _).Should().BeFalse();
        ft.DocumentCount.Should().Be(1);
    }

    [Fact]
    public void Japanese_bigrams_are_indexed()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        var ft = mgr.CreateFullTextIndex("idx_ja", "Doc", "body", MixedBigramTokenizer.DefaultTokenizerId);
        ft.AddDocument(1, Tok, "東京都"); // bigrams 東京, 京都

        ft.GetPostings("東京").Should().ContainSingle().Which.Item1.Should().Be(1L);
        ft.GetPostings("京都").Should().ContainSingle().Which.Item1.Should().Be(1L);
        ft.TryGetDocLength(1, out var dl).Should().BeTrue();
        dl.Should().Be(2);
    }

    // ---- catalog persistence ----

    [Fact]
    public void FullText_index_and_postings_survive_reopen()
    {
        using (var mgr = IndexManager.OpenStandalone(_dir))
        {
            var ft = mgr.CreateFullTextIndex("idx_text", "Doc", "body", MixedBigramTokenizer.DefaultTokenizerId);
            ft.AddDocument(1, Tok, "persisted document body");
        }

        using var reopened = IndexManager.OpenStandalone(_dir);
        reopened.ListFullTextIndexes().Should().ContainSingle()
            .Which.Should().Be(("idx_text", "Doc", "body", MixedBigramTokenizer.DefaultTokenizerId));

        reopened.TryGetFullTextIndex("idx_text", out var ft2).Should().BeTrue();
        ft2.GetPostings("persisted").Should().ContainSingle().Which.Item1.Should().Be(1L);

        reopened.TryGetFullTextIndexByLabelKey("Doc", "body", out var byBinding).Should().BeTrue();
        byBinding.Name.Should().Be("idx_text");
    }

    [Fact]
    public void Recreating_full_text_index_with_different_tokenizer_throws()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        mgr.CreateFullTextIndex("idx_text", "Doc", "body", "mixed-bigram-v1");
        Action act = () => mgr.CreateFullTextIndex("idx_text", "Doc", "body", "other-tok");
        act.Should().Throw<Quiver.Core.ConstraintException>();
    }

    [Fact]
    public void ResolveTokenizer_returns_registered_default()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        mgr.ResolveTokenizer("mixed-bigram-v1").TokenizerId.Should().Be("mixed-bigram-v1");
    }
}

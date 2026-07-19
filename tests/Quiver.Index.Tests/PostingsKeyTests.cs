using FluentAssertions;
using Quiver.Index.FullText;
using Xunit;

namespace Quiver.Index.Tests;

/// <summary>immutable全文segmentが使うterm key codecを検証する。</summary>
public sealed class PostingsKeyTests
{
    [Fact]
    public void Encode_roundtrips_term_and_entity_identity()
    {
        byte[] term = "検索"u8.ToArray();
        const long entityId = 123456789;

        byte[] encoded = PostingsKey.Encode(term, entityId);

        PostingsKey.DecodeTerm(encoded).Should().Be("検索");
        PostingsKey.DecodeEntityId(encoded).Should().Be(entityId);
    }

    [Fact]
    public void Term_range_contains_only_the_requested_term()
    {
        var (lower, upper) = PostingsKey.TermRange("alpha"u8);
        byte[] matching = PostingsKey.Encode("alpha"u8, 42);
        byte[] other = PostingsKey.Encode("alphabet"u8, 42);

        matching.AsSpan().SequenceCompareTo(lower).Should().BeGreaterThanOrEqualTo(0);
        matching.AsSpan().SequenceCompareTo(upper).Should().BeLessThanOrEqualTo(0);
        other.AsSpan().SequenceCompareTo(upper).Should().BeGreaterThan(0);
    }
}

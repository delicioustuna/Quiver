using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class FrontierSetTests
{
    [Fact]
    public void Dense_ids_use_bitmap_and_membership_works()
    {
        var fs = FrontierSet.Build([new VertexId(0), new VertexId(2), new VertexId(5)]);
        fs.Count.Should().Be(3);
        fs.Contains(new VertexId(0)).Should().BeTrue();
        fs.Contains(new VertexId(2)).Should().BeTrue();
        fs.Contains(new VertexId(5)).Should().BeTrue();
        fs.Contains(new VertexId(1)).Should().BeFalse();
        fs.Contains(new VertexId(6)).Should().BeFalse();
    }

    [Fact]
    public void Sparse_ids_use_hashset_and_membership_works()
    {
        // maxId much larger than count → triggers hash backing.
        var fs = FrontierSet.Build([new VertexId(0), new VertexId(1_000_000), new VertexId(2_000_000)]);
        fs.Count.Should().Be(3);
        fs.Contains(new VertexId(1_000_000)).Should().BeTrue();
        fs.Contains(new VertexId(999_999)).Should().BeFalse();
    }

    [Fact]
    public void Duplicate_ids_only_count_once()
    {
        var fs = FrontierSet.Build([new VertexId(7), new VertexId(7), new VertexId(7)]);
        fs.Count.Should().Be(1);
        fs.Contains(new VertexId(7)).Should().BeTrue();
    }

    [Fact]
    public void Dense_ids_distinguish_generation()
    {
        var first = VertexId.Create(7, 1);
        var second = VertexId.Create(7, 2);
        var fs = FrontierSet.Build([first, second]);

        fs.Count.Should().Be(2);
        fs.Contains(first).Should().BeTrue();
        fs.Contains(second).Should().BeTrue();
        fs.Contains(VertexId.Create(7, 3)).Should().BeFalse();
    }

    [Fact]
    public void Sparse_ids_distinguish_generation()
    {
        var first = VertexId.Create(1_000_000, 1);
        var second = VertexId.Create(1_000_000, 2);
        var fs = FrontierSet.Build([first, second]);

        fs.Count.Should().Be(2);
        fs.Contains(first).Should().BeTrue();
        fs.Contains(second).Should().BeTrue();
        fs.Contains(VertexId.Create(1_000_000, 3)).Should().BeFalse();
    }

    [Fact]
    public void Empty_frontier_contains_nothing()
    {
        var fs = FrontierSet.Build([]);
        fs.Count.Should().Be(0);
        fs.Contains(new VertexId(0)).Should().BeFalse();
    }

    [Fact]
    public void VertexBitSet_rejects_out_of_range_ids()
    {
        var bits = new VertexBitSet(capacity: 8);
        bits.Add(7);
        bits.Add(8);   // out of range: ignored
        bits.Add(-1);  // negative: ignored
        bits.Count.Should().Be(1);
        bits.Contains(7).Should().BeTrue();
        bits.Contains(8).Should().BeFalse();
    }
}

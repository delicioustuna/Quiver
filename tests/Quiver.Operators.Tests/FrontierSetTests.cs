using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class FrontierSetTests
{
    [Fact]
    public void Dense_ids_use_bitmap_and_membership_works()
    {
        var fs = FrontierSet.Build([new NodeId(0), new NodeId(2), new NodeId(5)]);
        fs.Count.Should().Be(3);
        fs.Contains(new NodeId(0)).Should().BeTrue();
        fs.Contains(new NodeId(2)).Should().BeTrue();
        fs.Contains(new NodeId(5)).Should().BeTrue();
        fs.Contains(new NodeId(1)).Should().BeFalse();
        fs.Contains(new NodeId(6)).Should().BeFalse();
    }

    [Fact]
    public void Sparse_ids_use_hashset_and_membership_works()
    {
        // maxId much larger than count → triggers hash backing.
        var fs = FrontierSet.Build([new NodeId(0), new NodeId(1_000_000), new NodeId(2_000_000)]);
        fs.Count.Should().Be(3);
        fs.Contains(new NodeId(1_000_000)).Should().BeTrue();
        fs.Contains(new NodeId(999_999)).Should().BeFalse();
    }

    [Fact]
    public void Duplicate_ids_only_count_once()
    {
        var fs = FrontierSet.Build([new NodeId(7), new NodeId(7), new NodeId(7)]);
        fs.Count.Should().Be(1);
        fs.Contains(new NodeId(7)).Should().BeTrue();
    }

    [Fact]
    public void Empty_frontier_contains_nothing()
    {
        var fs = FrontierSet.Build([]);
        fs.Count.Should().Be(0);
        fs.Contains(new NodeId(0)).Should().BeFalse();
    }

    [Fact]
    public void NodeBitSet_rejects_out_of_range_ids()
    {
        var bits = new NodeBitSet(capacity: 8);
        bits.Add(7);
        bits.Add(8);   // out of range: ignored
        bits.Add(-1);  // negative: ignored
        bits.Count.Should().Be(1);
        bits.Contains(7).Should().BeTrue();
        bits.Contains(8).Should().BeFalse();
    }
}

using FluentAssertions;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class VectorPayloadCacheTests
{
    [Fact]
    public void Zero_budget_disables_cache()
    {
        var cache = new VectorPayloadCache(4, new VectorPayloadCacheBudget(0));
        cache.StorePresent(0, 7, [1f, 2f, 3f, 4f]);

        Span<float> destination = stackalloc float[4];
        cache.TryGet(0, destination, out _).Should().Be(VectorPayloadCache.Lookup.Miss);
    }

    [Fact]
    public void Shared_budget_evicts_slab_across_indexes()
    {
        // 各 cache の slab は約 64 KiB。70 KB の DB 全体予算では 2 index 分を同時保持できない。
        var budget = new VectorPayloadCacheBudget(70_000);
        var first = new VectorPayloadCache(384, budget);
        var second = new VectorPayloadCache(384, budget);
        var vector = Enumerable.Repeat(0.25f, 384).ToArray();

        first.StorePresent(0, 1, vector);
        second.StorePresent(0, 2, vector);

        var destination = new float[384];
        first.TryGet(0, destination, out _).Should().Be(VectorPayloadCache.Lookup.Miss);
        second.TryGet(0, destination, out var generation)
            .Should().Be(VectorPayloadCache.Lookup.Present);
        generation.Should().Be(2);
        destination.Should().Equal(vector);
    }

    [Fact]
    public void Sparse_ranges_do_not_require_a_contiguous_array()
    {
        var cache = new VectorPayloadCache(4, new VectorPayloadCacheBudget(3 * 1024 * 1024));
        cache.StorePresent(1_000_000_000, 9, [4f, 3f, 2f, 1f]);

        Span<float> destination = stackalloc float[4];
        cache.TryGet(1_000_000_000, destination, out var generation)
            .Should().Be(VectorPayloadCache.Lookup.Present);
        generation.Should().Be(9);
        destination.ToArray().Should().Equal(4f, 3f, 2f, 1f);
    }
}

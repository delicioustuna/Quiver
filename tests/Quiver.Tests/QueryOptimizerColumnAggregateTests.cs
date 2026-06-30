using FluentAssertions;
using Quiver;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <see cref="QueryOptimizer.ShouldUseColumnAggregate"/> のコスト判定を検証する。
/// 列の密走査 (先頭版と差分) と行経路 (推定行数) のコストを比較する。
/// </summary>
public sealed class QueryOptimizerColumnAggregateTests
{
    [Fact]
    public void Prefers_column_when_delta_is_modest()
    {
        // head ≒ 行数、delta 少 → 列が圧倒的に安い。
        QueryOptimizer.ShouldUseColumnAggregate(columnHeadEntries: 10_000, deltaVersions: 50, estimatedRows: 10_000)
            .Should().BeTrue();
    }

    [Fact]
    public void Falls_back_to_row_when_delta_dominates()
    {
        // delta が肥大 (compaction 前) で列コストが row コストを超える → row path。
        QueryOptimizer.ShouldUseColumnAggregate(columnHeadEntries: 1_000, deltaVersions: 10_000_000, estimatedRows: 1_000)
            .Should().BeFalse();
    }

    [Fact]
    public void Empty_column_is_cheap()
    {
        QueryOptimizer.ShouldUseColumnAggregate(columnHeadEntries: 0, deltaVersions: 0, estimatedRows: 0)
            .Should().BeTrue();
    }
}

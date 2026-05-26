using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Operators.Tests.Support;
using Xunit;

namespace Quiver.Operators.Tests;

public class NodeIndexRangeScanOperatorTests
{
    [Fact]
    public void Empty_index_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.Db.Schema.CreateIndex("idx_v", "Item", "v", IndexKind.Int64Equality);
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new NodeIndexRangeScanOperator(
            "idx_v", LiteralProvider.Int64(0), true, LiteralProvider.Int64(100), true));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Range_returns_in_window()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.Db.Schema.CreateIndex("idx_v", "Item", "v", IndexKind.Int64Equality);
        using (var tx = fx.Db.BeginTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var n = tx.CreateNode("Item");
                tx.SetProperty(n, "v", PropertyValue.FromInt64(i));
                tx.IndexInsert("idx_v", (long)i, n);
            }
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new NodeIndexRangeScanOperator(
            "idx_v", LiteralProvider.Int64(1), true, LiteralProvider.Int64(3), true));
        result.Rows().Should().HaveCount(3); // 1, 2, 3
        tx2.Rollback();
    }

    [Fact]
    public void Exclusive_bounds_drop_endpoints()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.Db.Schema.CreateIndex("idx_v", "Item", "v", IndexKind.Int64Equality);
        using (var tx = fx.Db.BeginTransaction())
        {
            for (int i = 1; i <= 3; i++)
            {
                var n = tx.CreateNode("Item");
                tx.SetProperty(n, "v", PropertyValue.FromInt64(i));
                tx.IndexInsert("idx_v", (long)i, n);
            }
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new NodeIndexRangeScanOperator(
            "idx_v", LiteralProvider.Int64(1), false, LiteralProvider.Int64(3), false));
        result.Rows().Should().HaveCount(1); // only 2
        tx2.Rollback();
    }

    [Fact]
    public void Empty_window_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.Db.Schema.CreateIndex("idx_v", "Item", "v", IndexKind.Int64Equality);
        using (var tx = fx.Db.BeginTransaction())
        {
            var n = tx.CreateNode("Item");
            tx.SetProperty(n, "v", PropertyValue.FromInt64(5));
            tx.IndexInsert("idx_v", 5L, n);
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new NodeIndexRangeScanOperator(
            "idx_v", LiteralProvider.Int64(10), true, LiteralProvider.Int64(20), true));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}

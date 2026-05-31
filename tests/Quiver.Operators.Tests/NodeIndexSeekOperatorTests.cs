using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class NodeIndexSeekOperatorTests
{
    [Fact]
    public void Empty_index_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.Db.Schema.CreateIndex("idx", "Item", "n", IndexKind.Int64Equality);
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new NodeIndexSeekOperator(
            "idx", LiteralProvider.Int64(42)));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Seek_returns_matching_node()
    {
        NodeId target = default;
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.Db.Schema.CreateIndex("idx_v", "Item", "v", IndexKind.Int64Equality);
        using (var tx = fx.Db.BeginTransaction())
        {
            target = tx.CreateNode("Item");
            tx.SetProperty(target, "v", PropertyValue.FromInt64(7));
            tx.IndexInsert("idx_v", 7L, target);
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new NodeIndexSeekOperator(
            "idx_v", LiteralProvider.Int64(7)));
        result.Rows().Single().GetNodeId(0).Should().Be(target);
        tx2.Rollback();
    }

    [Fact]
    public void Seek_miss_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.Db.Schema.CreateIndex("idx_v", "Item", "v", IndexKind.Int64Equality);
        using (var tx = fx.Db.BeginTransaction())
        {
            var n = tx.CreateNode("Item");
            tx.SetProperty(n, "v", PropertyValue.FromInt64(1));
            tx.IndexInsert("idx_v", 1L, n);
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new NodeIndexSeekOperator(
            "idx_v", LiteralProvider.Int64(999)));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void String_key_seek_works()
    {
        NodeId target = default;
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.Db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);
        using (var tx = fx.Db.BeginTransaction())
        {
            target = tx.CreateNode("Person");
            tx.SetProperty(target, "name", PropertyValue.FromString("Alice"));
            tx.IndexInsert("idx_name", "Alice", target);
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new NodeIndexSeekOperator(
            "idx_name", LiteralProvider.String("Alice")));
        result.Rows().Single().GetNodeId(0).Should().Be(target);
        tx2.Rollback();
    }
}

using FluentAssertions;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class AllVerticesScanOperatorTests
{
    [Fact]
    public void Empty_database_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllVerticesScanOperator());
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Scans_every_vertex_when_no_label_filter()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateVertex("A");
            tx.CreateVertex("B");
            tx.CreateVertex("A");
        });
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllVerticesScanOperator());
        result.Rows().Should().HaveCount(3);
        tx.Rollback();
    }

    [Fact]
    public void Filters_by_label_when_given()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateVertex("A");
            tx.CreateVertex("B");
            tx.CreateVertex("A");
        });
        var labelA = fx.Db.Schema.GetOrCreateLabel("A");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllVerticesScanOperator(labelA));
        result.Rows().Should().HaveCount(2);
        tx.Rollback();
    }

    [Fact]
    public void Label_miss_yields_empty()
    {
        using var fx = OperatorTestFixture.Open(tx => { tx.CreateVertex("A"); });
        var labelB = fx.Db.Schema.GetOrCreateLabel("DoesNotExist");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllVerticesScanOperator(labelB));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Schema_has_single_VertexId_column()
    {
        var op = new AllVerticesScanOperator();
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
        op.Dispose();
    }
}

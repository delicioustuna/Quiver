using FluentAssertions;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class VertexByLabelScanOperatorTests
{
    [Fact]
    public void Empty_database_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var label = fx.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new VertexByLabelScanOperator(label));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Returns_only_vertices_with_matching_label()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateVertex("Person");
            tx.CreateVertex("Person");
            tx.CreateVertex("Movie");
            tx.CreateVertex("Person");
        });
        var label = fx.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new VertexByLabelScanOperator(label));
        result.Rows().Should().HaveCount(3);
        tx.Rollback();
    }

    [Fact]
    public void Unknown_label_returns_empty()
    {
        using var fx = OperatorTestFixture.Open(tx => { tx.CreateVertex("Existing"); });
        var unknown = fx.EditSchema(schema => schema.GetOrCreateLabel("NeverUsed"));
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new VertexByLabelScanOperator(unknown));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Schema_has_single_VertexId_column()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var label = fx.EditSchema(schema => schema.GetOrCreateLabel("X"));
        var op = new VertexByLabelScanOperator(label);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
        op.Dispose();
    }

    [Fact]
    public void Single_match_returns_one_row()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateVertex("Solo");
            tx.CreateVertex("Other");
            tx.CreateVertex("Other");
        });
        var label = fx.EditSchema(schema => schema.GetOrCreateLabel("Solo"));
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new VertexByLabelScanOperator(label));
        result.Rows().Should().HaveCount(1);
        tx.Rollback();
    }
}

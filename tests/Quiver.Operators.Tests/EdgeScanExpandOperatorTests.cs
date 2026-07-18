using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class EdgeScanExpandOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new EdgeScanExpandOperator(
            new FixedVertexListOperator(), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_outgoing_edge_emits_one_neighbor()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new EdgeScanExpandOperator(
            new FixedVertexListOperator(a), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().HaveCount(1);
        tx2.Rollback();
    }

    [Fact]
    public void Disconnected_source_yields_empty()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx => { a = tx.CreateVertex("X"); });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new EdgeScanExpandOperator(
            new FixedVertexListOperator(a), 0, Direction.Both, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}

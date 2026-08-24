using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Query.Physical.Tests.Support;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class EdgeEndpointOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new EdgeEndpointOperator(
            new FixedEdgeListOperator(), 0, EdgeEndpoint.Source));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Resolves_to_source_endpoint()
    {
        VertexId a = default, b = default;
        EdgeId edge = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            b = tx.CreateVertex("X");
            edge = tx.CreateEdge(a, b, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new EdgeEndpointOperator(
            new FixedEdgeListOperator(edge), 0, EdgeEndpoint.Source));
        result.Rows().Single().GetVertexId(0).Should().Be(a);
        tx2.Rollback();
    }

    [Fact]
    public void Resolves_to_target_endpoint()
    {
        VertexId a = default, b = default;
        EdgeId edge = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            b = tx.CreateVertex("X");
            edge = tx.CreateEdge(a, b, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new EdgeEndpointOperator(
            new FixedEdgeListOperator(edge), 0, EdgeEndpoint.Target));
        result.Rows().Single().GetVertexId(0).Should().Be(b);
        tx2.Rollback();
    }

    [Fact]
    public void Other_endpoint_defaults_to_target()
    {
        VertexId a = default, b = default;
        EdgeId edge = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            b = tx.CreateVertex("X");
            edge = tx.CreateEdge(a, b, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new EdgeEndpointOperator(
            new FixedEdgeListOperator(edge), 0, EdgeEndpoint.Other));
        result.Rows().Single().GetVertexId(0).Should().Be(b);
        tx2.Rollback();
    }
}

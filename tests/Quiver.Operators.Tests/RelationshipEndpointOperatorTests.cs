using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class RelationshipEndpointOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new RelationshipEndpointOperator(
            new FixedRelationshipListOperator(), 0, RelationshipEndpoint.Source));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Resolves_to_source_endpoint()
    {
        NodeId a = default, b = default;
        RelationshipId rel = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
            rel = tx.CreateRelationship(a, b, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new RelationshipEndpointOperator(
            new FixedRelationshipListOperator(rel), 0, RelationshipEndpoint.Source));
        result.Rows().Single().GetNodeId(0).Should().Be(a);
        tx2.Rollback();
    }

    [Fact]
    public void Resolves_to_target_endpoint()
    {
        NodeId a = default, b = default;
        RelationshipId rel = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
            rel = tx.CreateRelationship(a, b, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new RelationshipEndpointOperator(
            new FixedRelationshipListOperator(rel), 0, RelationshipEndpoint.Target));
        result.Rows().Single().GetNodeId(0).Should().Be(b);
        tx2.Rollback();
    }

    [Fact]
    public void Other_endpoint_defaults_to_target()
    {
        NodeId a = default, b = default;
        RelationshipId rel = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
            rel = tx.CreateRelationship(a, b, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new RelationshipEndpointOperator(
            new FixedRelationshipListOperator(rel), 0, RelationshipEndpoint.Other));
        result.Rows().Single().GetNodeId(0).Should().Be(b);
        tx2.Rollback();
    }
}

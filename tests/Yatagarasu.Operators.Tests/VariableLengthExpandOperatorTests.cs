using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Query.Physical.Tests.Support;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class VariableLengthExpandOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new VariableLengthExpandOperator(
            new FixedVertexListOperator(), 0, Direction.Outgoing, null, minHops: 1, maxHops: 3));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Chain_emits_all_within_hop_window()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            var c = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
            tx.CreateEdge(b, c, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new VariableLengthExpandOperator(
            new FixedVertexListOperator(a), 0, Direction.Outgoing, null, minHops: 1, maxHops: 2));
        result.Rows().Should().HaveCount(2); // b and c
        tx2.Rollback();
    }

    [Fact]
    public void MinHops_filters_short_paths()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            var c = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
            tx.CreateEdge(b, c, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new VariableLengthExpandOperator(
            new FixedVertexListOperator(a), 0, Direction.Outgoing, null, minHops: 2, maxHops: 2));
        result.Rows().Should().HaveCount(1); // only c
        tx2.Rollback();
    }

    [Fact]
    public void Isolated_vertex_returns_empty()
    {
        VertexId iso = default;
        using var fx = OperatorTestFixture.Open(tx => { iso = tx.CreateVertex("X"); });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new VariableLengthExpandOperator(
            new FixedVertexListOperator(iso), 0, Direction.Both, null, minHops: 1, maxHops: 3));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}

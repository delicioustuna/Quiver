using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class LabelNameLookupOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new LabelNameLookupOperator(
            new FixedNodeListOperator(), 0, _ => "X"));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Returns_label_name_for_each_row()
    {
        NodeId n = default;
        using var fx = OperatorTestFixture.Open(tx => { n = tx.CreateNode("Person"); });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new LabelNameLookupOperator(
            new FixedNodeListOperator(n), 0, id => fx.Db.Schema.GetLabelName(id)));
        result.Rows().Single().GetString(1).Should().Be("Person");
        tx2.Rollback();
    }

    [Fact]
    public void Unknown_label_lookup_yields_empty_string()
    {
        NodeId n = default;
        using var fx = OperatorTestFixture.Open(tx => { n = tx.CreateNode("Anything"); });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new LabelNameLookupOperator(
            new FixedNodeListOperator(n), 0, _ => null));
        result.Rows().Single().GetString(1).Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Result_schema_appends_label_string_column()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new LabelNameLookupOperator(
            new FixedNodeListOperator(), 0, _ => "?"));
        result.Schema.Columns.Should().HaveCount(2);
        result.Schema.Columns[1].Name.Should().Be("label");
        tx.Rollback();
    }

    [Fact]
    public void Null_lookup_throws()
    {
        Action act = () => new LabelNameLookupOperator(new FixedNodeListOperator(), 0, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

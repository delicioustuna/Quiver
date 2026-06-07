using FluentAssertions;

namespace Quiver.Api.Tests;

public class AttributesTest
{
    [Fact]
    public void NodeAttribute_stores_label()
    {
        var attr = new NodeAttribute("Person");
        attr.Label.Should().Be("Person");
    }

    [Fact]
    public void PropertyAttribute_stores_key()
    {
        var attr = new PropertyAttribute("name");
        attr.Key.Should().Be("name");
    }

    [Fact]
    public void IndexedAttribute_stores_index_name()
    {
        var attr = new IndexedAttribute("idx_name");
        attr.IndexName.Should().Be("idx_name");
    }

    [Fact]
    public void P_Gt_creates_predicate_correctly()
    {
        var pred = P.Gt(25L);
        pred.Kind.Should().Be(PredicateKind.Gt);
        pred.LongFrom.Should().Be(25L);
    }

    [Fact]
    public void P_Between_creates_predicate_correctly()
    {
        var pred = P.Between(10L, 20L);
        pred.Kind.Should().Be(PredicateKind.Between);
        pred.LongFrom.Should().Be(10L);
        pred.LongTo.Should().Be(20L);
    }
}

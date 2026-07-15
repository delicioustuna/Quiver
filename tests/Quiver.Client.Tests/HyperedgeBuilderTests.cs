using FluentAssertions;
using Quiver.Core;

namespace Quiver.Api.Tests;

/// <summary>ハイパーエッジ作成ビルダの入力規則と再利用境界を検証する。</summary>
public sealed class HyperedgeBuilderTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public HyperedgeBuilderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hyperedge_builder_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Builder_creates_multi_role_hyperedge_and_properties()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        NodeId alice = tx.CreateNode("Person");
        NodeId bob = tx.CreateNode("Person");
        NodeId book = tx.CreateNode("Book");

        HyperedgeId id = g.AddHyperedge("Purchase")
            .Member("buyer", alice)
            .Member("approver", alice)
            .Member("seller", bob)
            .Member("item", book)
            .P("status", "paid")
            .P("quantity", 2)
            .Next();

        g.Hyperedge(id).Members().Count().Should().Be(4);
        System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "status").Utf8StringValue)
            .Should().Be("paid");
        tx.GetProperty(id, "quantity").Int32Value.Should().Be(2);
    }

    [Fact]
    public void Builder_allows_multiple_nodes_in_the_same_role()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        NodeId a = tx.CreateNode("Person");
        NodeId b = tx.CreateNode("Person");

        HyperedgeId id = g.AddHyperedge("Meeting")
            .Member("attendee", a)
            .Member("attendee", b)
            .Next();

        g.Hyperedge(id).Members("attendee").Count().Should().Be(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Builder_rejects_an_empty_type(string type)
    {
        using var tx = _db.BeginTransaction();
        NodeId a = tx.CreateNode("Person");
        NodeId b = tx.CreateNode("Person");

        Action act = () => tx.G(_db.Schema).AddHyperedge(type)
            .Member("member", a)
            .Member("member", b)
            .Next();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Builder_rejects_arity_below_two()
    {
        using var tx = _db.BeginTransaction();
        NodeId a = tx.CreateNode("Person");

        Action act = () => tx.G(_db.Schema).AddHyperedge("Fact")
            .Member("subject", a)
            .Next();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Builder_rejects_duplicate_role_and_node_pair()
    {
        using var tx = _db.BeginTransaction();
        NodeId a = tx.CreateNode("Person");

        Action act = () => tx.G(_db.Schema).AddHyperedge("Fact")
            .Member("subject", a)
            .Member("subject", a)
            .Next();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Successful_next_clears_members_and_properties_for_reuse()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        NodeId a = tx.CreateNode("Person");
        NodeId b = tx.CreateNode("Person");
        NodeId c = tx.CreateNode("Person");
        var builder = g.AddHyperedge("Pair");

        HyperedgeId first = builder
            .Member("left", a)
            .Member("right", b)
            .P("batch", 1)
            .Next();
        HyperedgeId second = builder
            .Member("left", b)
            .Member("right", c)
            .Next();

        g.Hyperedge(first).Members().Count().Should().Be(2);
        g.Hyperedge(second).Members().Count().Should().Be(2);
        tx.HasProperty(first, "batch").Should().BeTrue();
        tx.HasProperty(second, "batch").Should().BeFalse();
    }
}

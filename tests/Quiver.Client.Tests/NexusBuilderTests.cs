using FluentAssertions;
using Quiver.Core;

namespace Quiver.Api.Tests;

/// <summary>Nexus作成ビルダの入力規則と再利用境界を検証する。</summary>
public sealed class NexusBuilderTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public NexusBuilderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_nexus_builder_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Builder_creates_multi_role_nexus_and_properties()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        VertexId alice = tx.CreateVertex("Person");
        VertexId bob = tx.CreateVertex("Person");
        VertexId book = tx.CreateVertex("Book");

        NexusId id = g.AddNexus("Purchase")
            .Member("buyer", alice)
            .Member("approver", alice)
            .Member("seller", bob)
            .Member("item", book)
            .P("status", "paid")
            .P("quantity", 2)
            .Next();

        g.Nexus(id).Members().Count().Should().Be(4);
        System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "status").Utf8StringValue)
            .Should().Be("paid");
        tx.GetProperty(id, "quantity").Int32Value.Should().Be(2);
    }

    [Fact]
    public void Builder_allows_multiple_vertices_in_the_same_role()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        VertexId a = tx.CreateVertex("Person");
        VertexId b = tx.CreateVertex("Person");

        NexusId id = g.AddNexus("Meeting")
            .Member("attendee", a)
            .Member("attendee", b)
            .Next();

        g.Nexus(id).Members("attendee").Count().Should().Be(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Builder_rejects_an_empty_type(string type)
    {
        using var tx = _db.BeginTransaction();
        VertexId a = tx.CreateVertex("Person");
        VertexId b = tx.CreateVertex("Person");

        Action act = () => tx.G(_db.Schema).AddNexus(type)
            .Member("member", a)
            .Member("member", b)
            .Next();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Builder_rejects_arity_below_two()
    {
        using var tx = _db.BeginTransaction();
        VertexId a = tx.CreateVertex("Person");

        Action act = () => tx.G(_db.Schema).AddNexus("Fact")
            .Member("subject", a)
            .Next();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Builder_rejects_duplicate_role_and_vertex_pair()
    {
        using var tx = _db.BeginTransaction();
        VertexId a = tx.CreateVertex("Person");

        Action act = () => tx.G(_db.Schema).AddNexus("Fact")
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
        VertexId a = tx.CreateVertex("Person");
        VertexId b = tx.CreateVertex("Person");
        VertexId c = tx.CreateVertex("Person");
        var builder = g.AddNexus("Pair");

        NexusId first = builder
            .Member("left", a)
            .Member("right", b)
            .P("batch", 1)
            .Next();
        NexusId second = builder
            .Member("left", b)
            .Member("right", c)
            .Next();

        g.Nexus(first).Members().Count().Should().Be(2);
        g.Nexus(second).Members().Count().Should().Be(2);
        tx.HasProperty(first, "batch").Should().BeTrue();
        tx.HasProperty(second, "batch").Should().BeFalse();
    }
}

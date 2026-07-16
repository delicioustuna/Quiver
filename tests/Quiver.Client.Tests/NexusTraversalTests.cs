using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>ロール付き多項関係を fluent traversal から走査できることを検証する。</summary>
public sealed class NexusTraversalTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;
    private readonly VertexId _alice;
    private readonly VertexId _bob;
    private readonly VertexId _book;
    private readonly NexusId _purchase;

    public NexusTraversalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_nexus_traversal_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        _alice = tx.CreateVertex("Person");
        _bob = tx.CreateVertex("Person");
        _book = tx.CreateVertex("Book");
        _purchase = tx.CreateNexus("Purchase",
        [
            new NexusMember("buyer", _alice),
            new NexusMember("approver", _alice),
            new NexusMember("seller", _bob),
            new NexusMember("item", _book),
        ]);
        tx.SetProperty(_purchase, "status", PropertyValue.FromString("paid"));
        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Scan_and_seed_return_nexuses()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        g.Nexuses().ToList().Should().ContainSingle().Which.Should().Be(_purchase);
        g.Nexus(_purchase).ToList().Should().ContainSingle().Which.Should().Be(_purchase);
    }

    [Fact]
    public void Nexuses_filters_by_type_and_origin_role()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        g.Vertex(_alice).Nexuses("Purchase", "buyer").ToList().Should().Equal(_purchase);
        g.Vertex(_alice).Nexuses("Purchase", "seller").ToList().Should().BeEmpty();
        g.Vertex(_alice).Nexuses("Missing").ToList().Should().BeEmpty();
    }

    [Fact]
    public void Members_filters_by_role()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        g.Nexus(_purchase).Members().ToList()
            .Should().BeEquivalentTo([_alice, _alice, _bob, _book]);
        g.Nexus(_purchase).Members("item").ToList().Should().Equal(_book);
    }

    [Fact]
    public void OtherMembers_excludes_origin_vertex_from_every_role()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var others = g.Vertex(_alice)
            .Nexuses("Purchase", "buyer")
            .OtherMembers()
            .ToList();

        others.Should().BeEquivalentTo([_bob, _book]);
        others.Should().NotContain(_alice);
    }

    [Fact]
    public void OtherMembers_requires_a_vertex_expansion_origin()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        Action fromSeed = () => g.Nexus(_purchase).OtherMembers();
        Action afterMembers = () => g.Vertex(_alice).Nexuses().Members().OtherMembers();

        fromSeed.Should().Throw<InvalidOperationException>();
        afterMembers.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Nexus_properties_remain_chainable_before_member_expansion()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        g.Nexuses().Has("status", "paid").Members("item").ToList().Should().Equal(_book);
        g.Nexuses().Values("status").ToList().Should().Equal("paid");
    }
}

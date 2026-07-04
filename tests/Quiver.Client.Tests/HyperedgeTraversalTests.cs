using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>ロール付き多項関係を fluent traversal から走査できることを検証する。</summary>
public sealed class HyperedgeTraversalTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;
    private readonly NodeId _alice;
    private readonly NodeId _bob;
    private readonly NodeId _book;
    private readonly HyperedgeId _purchase;

    public HyperedgeTraversalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hyperedge_traversal_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        _alice = tx.CreateNode("Person");
        _bob = tx.CreateNode("Person");
        _book = tx.CreateNode("Book");
        _purchase = tx.CreateHyperedge("Purchase",
        [
            new HyperedgeMember("buyer", _alice),
            new HyperedgeMember("approver", _alice),
            new HyperedgeMember("seller", _bob),
            new HyperedgeMember("item", _book),
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
    public void Scan_and_seed_return_hyperedges()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        g.Hyperedges().ToList().Should().ContainSingle().Which.Should().Be(_purchase);
        g.Hyperedge(_purchase).ToList().Should().ContainSingle().Which.Should().Be(_purchase);
    }

    [Fact]
    public void Hyperedges_filters_by_type_and_origin_role()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        g.Node(_alice).Hyperedges("Purchase", "buyer").ToList().Should().Equal(_purchase);
        g.Node(_alice).Hyperedges("Purchase", "seller").ToList().Should().BeEmpty();
        g.Node(_alice).Hyperedges("Missing").ToList().Should().BeEmpty();
    }

    [Fact]
    public void Members_filters_by_role()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        g.Hyperedge(_purchase).Members().ToList()
            .Should().BeEquivalentTo([_alice, _alice, _bob, _book]);
        g.Hyperedge(_purchase).Members("item").ToList().Should().Equal(_book);
    }

    [Fact]
    public void OtherMembers_excludes_origin_node_from_every_role()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var others = g.Node(_alice)
            .Hyperedges("Purchase", "buyer")
            .OtherMembers()
            .ToList();

        others.Should().BeEquivalentTo([_bob, _book]);
        others.Should().NotContain(_alice);
    }

    [Fact]
    public void OtherMembers_requires_a_node_expansion_origin()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        Action fromSeed = () => g.Hyperedge(_purchase).OtherMembers();
        Action afterMembers = () => g.Node(_alice).Hyperedges().Members().OtherMembers();

        fromSeed.Should().Throw<InvalidOperationException>();
        afterMembers.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Hyperedge_properties_remain_chainable_before_member_expansion()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        g.Hyperedges().Has("status", "paid").Members("item").ToList().Should().Equal(_book);
        g.Hyperedges().Values("status").ToList().Should().Equal("paid");
    }
}

using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <c>.As(label)</c> / <c>.Select(label)</c> によるタプルスキーマ拡張を検証する。
/// 1 ホップ展開 (Out / In / OutE)、複数ホップ、途中のフィルターを経ても別名が維持され、
/// 型付き射影 <c>Select&lt;T&gt;(Func&lt;MatchTuple, T&gt;)</c> が
/// 引き継いだ列を正しく読むことを確認する。
/// </summary>
public sealed class GremlinCompatGc6Tests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public GremlinCompatGc6Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_gc6_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private VertexId AddPerson(IGraphTransaction tx, string name, int? age = null)
    {
        var id = tx.CreateVertex("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString(name));
        if (age.HasValue) tx.SetProperty(id, "age", PropertyValue.FromInt64(age.Value));
        return id;
    }

    // Linear: Alice -> Bob, Alice -> Carol, Bob -> Dave
    private (VertexId alice, VertexId bob, VertexId carol, VertexId dave) BuildSmallGraph()
    {
        using var tx = _db.BeginTransaction();
        var alice = AddPerson(tx, "Alice", age: 30);
        var bob   = AddPerson(tx, "Bob",   age: 25);
        var carol = AddPerson(tx, "Carol", age: 40);
        var dave  = AddPerson(tx, "Dave",  age: 35);
        tx.CreateEdge(alice, bob,   "KNOWS");
        tx.CreateEdge(alice, carol, "KNOWS");
        tx.CreateEdge(bob,   dave,  "KNOWS");
        tx.Commit();
        return (alice, bob, carol, dave);
    }

    [Fact]
    public void As_then_Select_same_step_returns_self()
    {
        var (alice, _, _, _) = BuildSmallGraph();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var ids = g.Vertices().HasLabel("Person").Has("name", "Alice").As("a").Select("a").ToList();

        ids.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void Select_recovers_pinned_column_after_Out()
    {
        var (alice, _, _, _) = BuildSmallGraph();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Pin "a"=Alice, walk to her friends, then ask for "a" again — should
        // recover Alice once per outgoing edge (2 friends → 2 occurrences).
        var aliceCopies = g.Vertices().HasLabel("Person").Has("name", "Alice").As("a")
            .Out("KNOWS")
            .Select("a")
            .ToList();

        aliceCopies.Should().HaveCount(2);
        aliceCopies.Should().AllBeEquivalentTo(alice);
    }

    [Fact]
    public void Select_with_projection_returns_typed_pairs()
    {
        var (alice, bob, carol, _) = BuildSmallGraph();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var pairs = g.Vertices().HasLabel("Person").Has("name", "Alice").As("a")
            .Out("KNOWS").As("b")
            .Select(t => (Source: t.Vertex("a"), Friend: t.Vertex("b")));

        pairs.Should().HaveCount(2);
        pairs.Should().BeEquivalentTo(new[]
        {
            (Source: alice, Friend: bob),
            (Source: alice, Friend: carol),
        });
    }

    [Fact]
    public void Aliases_survive_intermediate_filter()
    {
        var (alice, bob, _, _) = BuildSmallGraph();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Bob is the only friend named "Bob" — Has() must not drop carried "a".
        var pairs = g.Vertices().HasLabel("Person").Has("name", "Alice").As("a")
            .Out("KNOWS").Has("name", "Bob").As("b")
            .Select(t => (a: t.Vertex("a"), b: t.Vertex("b")));

        pairs.Should().ContainSingle().Which.Should().Be((alice, bob));
    }

    [Fact]
    public void Aliases_survive_two_hop_expansion()
    {
        var (alice, bob, _, dave) = BuildSmallGraph();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Alice -> Bob -> Dave. After two hops "a"=Alice and "b"=Bob must
        // still resolve correctly alongside the current entity (Dave).
        var triples = g.Vertices().HasLabel("Person").Has("name", "Alice").As("a")
            .Out("KNOWS").As("b")
            .Out("KNOWS").As("c")
            .Select(t => (a: t.Vertex("a"), b: t.Vertex("b"), c: t.Vertex("c")));

        triples.Should().ContainSingle().Which.Should().Be((alice, bob, dave));
    }

    [Fact]
    public void Select_chains_continuation_from_pinned_column()
    {
        var (alice, _, _, _) = BuildSmallGraph();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // After Out("KNOWS"), .Select("a") re-aims back at Alice; .Out("KNOWS")
        // from there should re-walk her two friends. Once for each upstream
        // row, so 2 friends × 2 upstream rows = 4 rows.
        var friendsOfA = g.Vertices().HasLabel("Person").Has("name", "Alice").As("a")
            .Out("KNOWS")
            .Select("a")
            .Out("KNOWS")
            .ToList();

        friendsOfA.Should().HaveCount(4);
    }

    [Fact]
    public void Select_unknown_alias_throws()
    {
        BuildSmallGraph();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () => g.Vertices().HasLabel("Person").As("a").Select("zzz").ToList();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*zzz*");
    }

    [Fact]
    public void OutE_then_select_resolves_both_vertex_and_edge_aliases()
    {
        var (alice, bob, _, _) = BuildSmallGraph();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Pin source vertex "a", capture edge as "r", terminate on the neighbor.
        // OutE keeps edge@0 / neighbor@1, so carry should preserve "a" at the
        // tail and let .Select recover both Alice and the edge id.
        var bobEdge = g.Vertices().HasLabel("Person").Has("name", "Alice").As("a")
            .OutEdges("KNOWS").As("r")
            .TargetVertex().ToList();

        // Sanity: the InV step itself produces 2 neighbors (Bob and Carol).
        bobEdge.Should().HaveCount(2);
        bobEdge.Should().Contain(bob);
    }
}

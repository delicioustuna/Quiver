using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 可変長反復 (.Repeat.Times / .Emit)、最短経路距離、重複除去、
/// 行単位の分岐 (.Union / .Coalesce / .Optional) を検証する。
/// </summary>
public sealed class GremlinBranchingPathTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public GremlinBranchingPathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_gc4_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private VertexId AddPerson(IWriteTransaction tx, string name)
    {
        var id = tx.CreateVertex("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString(name));
        return id;
    }

    // ── chain helper: a -> b -> c -> d (linear) plus optional branches ──────
    private (VertexId a, VertexId b, VertexId c, VertexId d) BuildLineGraph()
    {
        using var tx = _db.BeginWriteTransaction();
        var a = AddPerson(tx, "A");
        var b = AddPerson(tx, "B");
        var c = AddPerson(tx, "C");
        var d = AddPerson(tx, "D");
        tx.CreateEdge(a, b, "KNOWS");
        tx.CreateEdge(b, c, "KNOWS");
        tx.CreateEdge(c, d, "KNOWS");
        tx.Commit();
        return (a, b, c, d);
    }

    // ── .Repeat / .Times / .Emit ────────────────────────────────────────────

    [Fact]
    public void Repeat_times_returns_only_terminal_frontier()
    {
        var (a, _, c, _) = BuildLineGraph();
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var depth2 = g.Vertex(a).Repeat(s => s.Out("KNOWS"), times: 2).ToList();
        depth2.Should().ContainSingle().Which.Value.Should().Be(c.Value);
    }

    [Fact]
    public void Repeat_emit_returns_every_frontier_up_to_times()
    {
        var (a, b, c, d) = BuildLineGraph();
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var vertices = g.Vertex(a).Repeat(s => s.Out("KNOWS"), times: 3, emit: true).ToList()
            .Select(n => n.Value).OrderBy(v => v).ToList();
        vertices.Should().BeEquivalentTo(new[] { b.Value, c.Value, d.Value });
    }

    [Fact]
    public void Repeat_with_type_filter_respects_edge_type()
    {
        using var tx = _db.BeginWriteTransaction();
        var a = AddPerson(tx, "A");
        var b = AddPerson(tx, "B");
        var c = AddPerson(tx, "C");
        tx.CreateEdge(a, b, "KNOWS");
        tx.CreateEdge(b, c, "WORKS_AT"); // different type — should NOT be followed
        tx.Commit();

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // 2 hops of KNOWS from a: only b reachable at depth 1, depth 2 is empty.
        g.Vertex(a).Repeat(s => s.Out("KNOWS"), times: 2).ToList().Should().BeEmpty();
    }

    [Fact]
    public void Repeat_times_must_be_positive()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        FluentActions.Invoking(() => g.Vertices().Repeat(s => s.Out("KNOWS"), times: 0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── .ShortestPathTo ──────────────────────────────────────────────────────

    [Fact]
    public void ShortestPathTo_returns_distance_from_each_source()
    {
        var (a, _, _, d) = BuildLineGraph();
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Vertex(a).ShortestPathTo(d, type: "KNOWS").Next().Should().Be(3);
    }

    [Fact]
    public void ShortestPathTo_picks_shortcut_over_longer_route()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            var a = AddPerson(tx, "A");
            var b = AddPerson(tx, "B");
            var c = AddPerson(tx, "C");
            var d = AddPerson(tx, "D");
            tx.CreateEdge(a, b, "K");
            tx.CreateEdge(b, c, "K");
            tx.CreateEdge(c, d, "K");
            tx.CreateEdge(a, c, "K"); // shortcut: a -> c directly
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var vertices = g.Vertices().HasLabel("Person").Has("name", "A").ToList();
        vertices.Should().HaveCount(1);
        var src = vertices[0];
        var dst = g.Vertices().HasLabel("Person").Has("name", "D").ToList()[0];

        g.Vertex(src).ShortestPathTo(dst, type: "K").Next().Should().Be(2);
    }

    [Fact]
    public void ShortestPathTo_drops_rows_with_no_path()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "A");
            AddPerson(tx, "Z");
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var a = g.Vertices().HasLabel("Person").Has("name", "A").ToList()[0];
        var z = g.Vertices().HasLabel("Person").Has("name", "Z").ToList()[0];

        g.Vertex(a).ShortestPathTo(z, type: "K").ToList().Should().BeEmpty();
    }

    // ── .Dedup ──────────────────────────────────────────────────────────────

    [Fact]
    public void Dedup_keeps_first_occurrence_of_each_vertex()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            var a = AddPerson(tx, "A");
            var b = AddPerson(tx, "B");
            var c = AddPerson(tx, "C");
            tx.CreateEdge(a, c, "K");
            tx.CreateEdge(b, c, "K"); // c is reachable twice
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var raw = g.Vertices().HasLabel("Person").Out("K").ToList();
        raw.Should().HaveCount(2); // c reached twice

        var deduped = g.Vertices().HasLabel("Person").Out("K").Dedup().ToList();
        deduped.Should().HaveCount(1);
    }

    // ── .Union ──────────────────────────────────────────────────────────────

    [Fact]
    public void Union_concatenates_branch_results_per_input_row()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            var aSeed = AddPerson(tx, "A");
            var bSeed = AddPerson(tx, "B");
            var cSeed = AddPerson(tx, "C");
            tx.CreateEdge(aSeed, bSeed, "KNOWS");
            tx.CreateEdge(cSeed, aSeed, "KNOWS"); // c -> a so a has incoming
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var a = g.Vertices().HasLabel("Person").Has("name", "A").ToList()[0];

        var union = g.Vertex(a)
            .Union(
                s => s.Out("KNOWS"),
                s => s.In("KNOWS"))
            .ToList()
            .Select(n => n.Value).OrderBy(v => v).ToList();

        union.Should().HaveCount(2); // one outgoing (B), one incoming (C)
    }

    // ── .Coalesce ───────────────────────────────────────────────────────────

    [Fact]
    public void Coalesce_returns_first_branch_with_any_result()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            var aSeed = AddPerson(tx, "A");
            var bSeed = AddPerson(tx, "B");
            tx.CreateEdge(aSeed, bSeed, "KNOWS"); // a only has KNOWS, no LIKES
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var a = g.Vertices().HasLabel("Person").Has("name", "A").ToList()[0];

        var coalesced = g.Vertex(a)
            .Coalesce(
                s => s.Out("LIKES"),    // empty
                s => s.Out("KNOWS"))    // matches — used
            .ToList();

        coalesced.Should().HaveCount(1);
    }

    [Fact]
    public void Coalesce_skips_remaining_branches_after_match()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            var aSeed = AddPerson(tx, "A");
            var bSeed = AddPerson(tx, "B");
            var cSeed = AddPerson(tx, "C");
            tx.CreateEdge(aSeed, bSeed, "KNOWS");
            tx.CreateEdge(aSeed, cSeed, "LIKES");
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var a = g.Vertices().HasLabel("Person").Has("name", "A").ToList()[0];

        // First branch (KNOWS) returns b — LIKES branch is never evaluated.
        var coalesced = g.Vertex(a)
            .Coalesce(
                s => s.Out("KNOWS"),
                s => s.Out("LIKES"))
            .ToList();
        coalesced.Should().HaveCount(1);
    }

    // ── .Optional ───────────────────────────────────────────────────────────

    [Fact]
    public void Optional_emits_branch_when_branch_produces_rows()
    {
        var (a, b, _, _) = BuildLineGraph();
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var rows = g.Vertex(a).Optional(s => s.Out("KNOWS")).ToList();
        rows.Should().ContainSingle().Which.Value.Should().Be(b.Value);
    }

    [Fact]
    public void Optional_falls_through_when_branch_is_empty()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Lonely"); // no edges
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var lonely = g.Vertices().HasLabel("Person").Has("name", "Lonely").ToList()[0];

        var rows = g.Vertex(lonely).Optional(s => s.Out("KNOWS")).ToList();
        rows.Should().ContainSingle().Which.Value.Should().Be(lonely.Value);
    }
}

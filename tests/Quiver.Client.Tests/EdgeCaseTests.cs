using FluentAssertions;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>
/// エッジケーステスト。null 引数、存在しないラベル/プロパティ、
/// 空グラフ、境界条件などを検証する。
/// </summary>
public sealed class EdgeCaseTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public EdgeCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_edge_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── 空グラフ ─────────────────────────────────────────────────────

    [Fact]
    public void Nodes_on_empty_graph_returns_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().ToList().Should().BeEmpty();
    }

    [Fact]
    public void Relationships_on_empty_graph_returns_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Relationships().ToList().Should().BeEmpty();
    }

    [Fact]
    public void Count_on_empty_graph_returns_zero()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().Count().Should().Be(0);
    }

    [Fact]
    public void HasNext_on_empty_graph_returns_false()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasNext().Should().BeFalse();
    }

    // ── 存在しない label / property ─────────────────────────────────

    [Fact]
    public void HasLabel_nonexistent_label_returns_empty()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("NoSuchLabel").ToList().Should().BeEmpty();
    }

    [Fact]
    public void Has_nonexistent_property_returns_empty()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Test").Has("NoSuchProp", "value").ToList().Should().BeEmpty();
    }

    [Fact]
    public void Out_nonexistent_type_returns_empty()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var node = g.Nodes().HasLabel("Test").Next();
        g.Node(node).Out("NoSuchType").ToList().Should().BeEmpty();
    }

    [Fact]
    public void Values_nonexistent_property_returns_empty()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var values = g.Nodes().HasLabel("Test").Values("MissingProp").ToList();
        values.Should().ContainSingle().Which.Should().BeEmpty();
    }

    // ── 空結果への集約 ───────────────────────────────────────────────

    [Fact]
    public void Sum_on_empty_returns_zero()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Ghost").Sum("Age").Should().Be(0.0);
    }

    [Fact]
    public void Max_on_empty_returns_null()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Ghost").Max("Age").Should().BeNull();
    }

    [Fact]
    public void Min_on_empty_returns_null()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Ghost").Min("Age").Should().BeNull();
    }

    [Fact]
    public void Mean_on_empty_returns_null()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Ghost").Mean("Age").Should().BeNull();
    }

    [Fact]
    public void GroupCount_on_empty_returns_empty_dict()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Ghost").GroupCount("Name").Should().BeEmpty();
    }

    // ── 引数検証 ─────────────────────────────────────────────────────

    [Fact]
    public void And_empty_traversals_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().And();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Or_empty_traversals_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().Or();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void As_null_or_empty_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().As("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Select_null_or_empty_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().Select("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Select_projection_without_As_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().Select(t => t.Node("a"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void OrderBy_null_key_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().OrderBy(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Repeat_null_step_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().Repeat(null!, times: 1);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Skip_negative_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().Skip(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Range_invalid_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // to < from は不正
        var act = () => g.Nodes().Range(5, 3);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Range_negative_from_throws()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().Range(-1, 5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Limit の境界条件 ─────────────────────────────────────────────

    [Fact]
    public void Limit_zero_returns_empty()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().Limit(0).ToList().Should().BeEmpty();
    }

    [Fact]
    public void Limit_larger_than_result_returns_all()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Nodes().ToList();
        var limited = g.Nodes().Limit(100).ToList();
        limited.Should().HaveCount(all.Count);
    }

    // ── 単一ノードグラフの境界条件 ─────────────────────────────────

    [Fact]
    public void Out_on_isolated_node_returns_empty()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var node = g.Nodes().HasLabel("Test").Next();
        g.Node(node).Out().ToList().Should().BeEmpty();
    }

    [Fact]
    public void In_on_isolated_node_returns_empty()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var node = g.Nodes().HasLabel("Test").Next();
        g.Node(node).In().ToList().Should().BeEmpty();
    }

    [Fact]
    public void Both_on_isolated_node_returns_empty()
    {
        SeedSingleNode();
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var node = g.Nodes().HasLabel("Test").Next();
        g.Node(node).Both().ToList().Should().BeEmpty();
    }

    // ── P factory の境界条件 ─────────────────────────────────────────

    [Fact]
    public void P_StartsWith_null_throws()
    {
        var act = () => P.StartsWith(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void P_EndsWith_null_throws()
    {
        var act = () => P.EndsWith(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void P_Contains_null_throws()
    {
        var act = () => P.Contains(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void P_Regex_null_throws()
    {
        var act = () => P.Regex(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void P_Not_null_throws()
    {
        var act = () => P.Not(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void P_And_empty_throws()
    {
        var act = () => P.And();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void P_Or_empty_throws()
    {
        var act = () => P.Or();
        act.Should().Throw<ArgumentException>();
    }

    // ── ヘルパー ─────────────────────────────────────────────────────

    private void SeedSingleNode()
    {
        using var tx = _db.BeginTransaction();
        var n = tx.CreateNode("Test");
        tx.SetProperty(n, "Name", PropertyValue.FromString("TestNode"));
        tx.Commit();
    }
}

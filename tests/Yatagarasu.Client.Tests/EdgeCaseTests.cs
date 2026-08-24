using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Api.Match;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Api.Tests;

/// <summary>
/// エッジケーステスト。null 引数、存在しないラベル/プロパティ、
/// 空グラフ、境界条件などを検証する。
/// </summary>
public sealed class EdgeCaseTests : IDisposable
{
    private readonly string _dir;
    private readonly YatagarasuDatabase _db;

    public EdgeCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_edge_" + Guid.NewGuid().ToString("N"));
        _db = YatagarasuDatabase.Open(Path.Combine(_dir, "graph.yata"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── 空グラフ ─────────────────────────────────────────────────────

    [Fact]
    public void Vertices_on_empty_graph_returns_empty()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().ToList().Should().BeEmpty();
    }

    [Fact]
    public void Edges_on_empty_graph_returns_empty()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Edges().ToList().Should().BeEmpty();
    }

    [Fact]
    public void Count_on_empty_graph_returns_zero()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().Count().Should().Be(0);
    }

    [Fact]
    public void HasNext_on_empty_graph_returns_false()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().HasNext().Should().BeFalse();
    }

    // ── 存在しない label / property ─────────────────────────────────

    [Fact]
    public void HasLabel_nonexistent_label_returns_empty()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().HasLabel("NoSuchLabel").ToList().Should().BeEmpty();
    }

    [Fact]
    public void Has_nonexistent_property_returns_empty()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().HasLabel("Test").Has("NoSuchProp", "value").ToList().Should().BeEmpty();
    }

    [Fact]
    public void Out_nonexistent_type_returns_empty()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var vertex = g.Vertices().HasLabel("Test").Next();
        g.Vertex(vertex).Out("NoSuchType").ToList().Should().BeEmpty();
    }

    [Fact]
    public void Values_nonexistent_property_returns_empty()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var values = g.Vertices().HasLabel("Test").Values("MissingProp").ToList();
        values.Should().ContainSingle().Which.Should().BeEmpty();
    }

    // ── 空結果への集約 ───────────────────────────────────────────────

    [Fact]
    public void Sum_on_empty_returns_zero()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().HasLabel("Ghost").Sum("Age").Should().Be(0.0);
    }

    [Fact]
    public void Max_on_empty_returns_null()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().HasLabel("Ghost").Max("Age").Should().BeNull();
    }

    [Fact]
    public void Min_on_empty_returns_null()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().HasLabel("Ghost").Min("Age").Should().BeNull();
    }

    [Fact]
    public void Mean_on_empty_returns_null()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().HasLabel("Ghost").Mean("Age").Should().BeNull();
    }

    [Fact]
    public void GroupCount_on_empty_returns_empty_dict()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().HasLabel("Ghost").GroupCount("Name").Should().BeEmpty();
    }

    // ── 引数検証 ─────────────────────────────────────────────────────

    [Fact]
    public void And_empty_traversals_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().And();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Or_empty_traversals_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().Or();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void As_null_or_empty_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().As("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Select_null_or_empty_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().Select("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Select_projection_without_As_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().Select(t => t.Vertex("a"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void OrderBy_null_key_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().OrderBy(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Repeat_null_step_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().Repeat(null!, times: 1);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Skip_negative_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().Skip(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Range_invalid_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        // to < from は不正
        var act = () => g.Vertices().Range(5, 3);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Range_negative_from_throws()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var act = () => g.Vertices().Range(-1, 5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Limit の境界条件 ─────────────────────────────────────────────

    [Fact]
    public void Limit_zero_returns_empty()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        g.Vertices().Limit(0).ToList().Should().BeEmpty();
    }

    [Fact]
    public void Limit_larger_than_result_returns_all()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var all = g.Vertices().ToList();
        var limited = g.Vertices().Limit(100).ToList();
        limited.Should().HaveCount(all.Count);
    }

    // ── 単一Vertexグラフの境界条件 ─────────────────────────────────

    [Fact]
    public void Out_on_isolated_vertex_returns_empty()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var vertex = g.Vertices().HasLabel("Test").Next();
        g.Vertex(vertex).Out().ToList().Should().BeEmpty();
    }

    [Fact]
    public void In_on_isolated_vertex_returns_empty()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var vertex = g.Vertices().HasLabel("Test").Next();
        g.Vertex(vertex).In().ToList().Should().BeEmpty();
    }

    [Fact]
    public void Both_on_isolated_vertex_returns_empty()
    {
        SeedSingleVertex();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;
        var vertex = g.Vertices().HasLabel("Test").Next();
        g.Vertex(vertex).Both().ToList().Should().BeEmpty();
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

    private void SeedSingleVertex()
    {
        using var tx = _db.BeginWriteTransaction();
        var n = tx.CreateVertex("Test");
        tx.SetProperty(n, "Name", PropertyValue.FromString("TestVertex"));
        tx.Commit();
    }
}

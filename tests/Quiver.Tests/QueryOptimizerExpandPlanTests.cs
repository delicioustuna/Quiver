using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <c>QueryOptimizer.SelectExpandPlan</c> が AdjacencyBlock、LinkedListChain、
/// EdgeScan の 3 経路を選択する処理を検証する。
/// </summary>
public sealed class QueryOptimizerExpandPlanTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public QueryOptimizerExpandPlanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_opt_pw17_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Without_frontierSize_returns_AdjacencyBlock()
    {
        SeedSmallGraph();
        var opt = new QueryOptimizer(_db.CollectStats());

        var plan = opt.SelectExpandPlan(sourceLabel: null, typeFilter: null, Direction.Outgoing);

        plan.Strategy.Should().Be(ExpandStrategy.AdjacencyBlock);
    }

    [Fact]
    public void Small_frontier_returns_AdjacencyBlock()
    {
        SeedSmallGraph();
        var opt = new QueryOptimizer(_db.CollectStats());

        var plan = opt.SelectExpandPlan(
            sourceLabel: null, typeFilter: null, Direction.Outgoing, frontierSize: 1);

        plan.Strategy.Should().Be(ExpandStrategy.AdjacencyBlock);
    }

    [Fact]
    public void Large_frontier_relative_to_total_returns_EdgeScan()
    {
        SeedSmallGraph();
        var stats = _db.CollectStats();
        var opt = new QueryOptimizer(stats);

        // Frontier covers (almost) the whole graph → per-vertex probes ≈ TotalEdges.
        var plan = opt.SelectExpandPlan(
            sourceLabel: null, typeFilter: null, Direction.Outgoing,
            frontierSize: stats.TotalVertices);

        plan.Strategy.Should().Be(ExpandStrategy.EdgeScan);
    }

    [Fact]
    public void Plan_Build_returns_correct_operator_type()
    {
        SeedSmallGraph();
        var scanPlan = new ExpandPlan(ExpandStrategy.EdgeScan, EstimatedFanOut: 0);
        var src = new AllVerticesScanOperator(null);
        using var op = scanPlan.Build(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        op.Should().BeOfType<EdgeScanExpandOperator>();

        var blockPlan = new ExpandPlan(ExpandStrategy.AdjacencyBlock, EstimatedFanOut: 0);
        var src2 = new AllVerticesScanOperator(null);
        using var op2 = blockPlan.Build(src2, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        op2.Should().BeOfType<ExpandOperator>();
    }

    private void SeedSmallGraph()
    {
        // Enough edges that a frontier of 1 vertex only scratches the surface
        // (well below the 25% scan threshold) but a frontier covering all vertices
        // clearly exceeds it.
        using var tx = _db.BeginTransaction();
        var vertices = new VertexId[40];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = tx.CreateVertex("Person");
        for (int i = 0; i < vertices.Length; i++)
            for (int j = 0; j < 3; j++)
                tx.CreateEdge(vertices[i], vertices[(i + j + 1) % vertices.Length], "KNOWS");
        tx.Commit();
    }
}

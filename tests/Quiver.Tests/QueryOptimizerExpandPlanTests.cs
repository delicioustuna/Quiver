using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// PW-17: QueryOptimizer.SelectExpandPlan three-way dispatch
/// (AdjacencyBlock / LinkedListChain / RelationshipScan).
/// </summary>
public sealed class QueryOptimizerExpandPlanTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public QueryOptimizerExpandPlanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_opt_pw17_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(_dir);
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
    public void Large_frontier_relative_to_total_returns_RelationshipScan()
    {
        SeedSmallGraph();
        var stats = _db.CollectStats();
        var opt = new QueryOptimizer(stats);

        // Frontier covers (almost) the whole graph → per-node probes ≈ TotalRelationships.
        var plan = opt.SelectExpandPlan(
            sourceLabel: null, typeFilter: null, Direction.Outgoing,
            frontierSize: stats.TotalNodes);

        plan.Strategy.Should().Be(ExpandStrategy.RelationshipScan);
    }

    [Fact]
    public void Plan_Build_returns_correct_operator_type()
    {
        SeedSmallGraph();
        var scanPlan = new ExpandPlan(ExpandStrategy.RelationshipScan, EstimatedFanOut: 0);
        var src = new AllNodesScanOperator(null);
        using var op = scanPlan.Build(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        op.Should().BeOfType<RelationshipScanExpandOperator>();

        var blockPlan = new ExpandPlan(ExpandStrategy.AdjacencyBlock, EstimatedFanOut: 0);
        var src2 = new AllNodesScanOperator(null);
        using var op2 = blockPlan.Build(src2, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        op2.Should().BeOfType<ExpandOperator>();
    }

    private void SeedSmallGraph()
    {
        // Enough relationships that a frontier of 1 node only scratches the surface
        // (well below the 25% scan threshold) but a frontier covering all nodes
        // clearly exceeds it.
        using var tx = _db.BeginTransaction();
        var nodes = new NodeId[40];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = tx.CreateNode("Person");
        for (int i = 0; i < nodes.Length; i++)
            for (int j = 0; j < 3; j++)
                tx.CreateRelationship(nodes[i], nodes[(i + j + 1) % nodes.Length], "KNOWS");
        tx.Commit();
    }
}

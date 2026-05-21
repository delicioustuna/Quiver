using FluentAssertions;
using Quiver.Client;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 重み付き Dijkstra / A* 最短経路 — <see cref="WeightedShortestPathOperator"/> と
/// <see cref="GraphTraversalSource"/> の公開 API のカバレッジ。
/// </summary>
public sealed class WeightedShortestPathTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public WeightedShortestPathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_wsp_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(_dir);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---------------------------------------------------------- operator layer --

    [Fact]
    public void Dijkstra_picks_lower_weight_multi_hop_over_direct_edge()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        // 直行 a->b は重み 10、迂回 a->c->b は重み 1+1=2。
        SetWeight(tx, tx.CreateRelationship(a, b, "K"), 10.0);
        var ac = tx.CreateRelationship(a, c, "K"); SetWeight(tx, ac, 1.0);
        var cb = tx.CreateRelationship(c, b, "K"); SetWeight(tx, cb, 1.0);

        var (dist, nodes, rels) = RunOperator(tx, a, b);

        dist.Should().Be(2.0);
        nodes.Should().Equal(a, c, b);
        rels.Should().Equal(ac, cb);
        tx.Rollback();
    }

    [Fact]
    public void Dijkstra_self_pair_returns_zero_distance_and_single_node_path()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");

        var (dist, nodes, rels) = RunOperator(tx, a, a);

        dist.Should().Be(0.0);
        nodes.Should().Equal(a);
        rels.Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Dijkstra_no_path_emits_no_row()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");   // a と b の間にエッジ無し

        var keyId = _db.Schema.GetOrCreatePropertyKey("w");
        using var op = new WeightedShortestPathOperator(
            new PairSource(a, b), 0, 1, Direction.Outgoing, null,
            new PropertyChainWeightProvider(keyId));
        using var result = tx.Execute(op);

        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Negative_edge_weight_throws()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        SetWeight(tx, tx.CreateRelationship(a, b, "K"), -1.0);

        var keyId = _db.Schema.GetOrCreatePropertyKey("w");
        using var op = new WeightedShortestPathOperator(
            new PairSource(a, b), 0, 1, Direction.Outgoing, null,
            new PropertyChainWeightProvider(keyId));

        var act = () => tx.Execute(op);
        act.Should().Throw<InvalidOperationException>().WithMessage("*負のエッジ重み*");
        tx.Rollback();
    }

    [Fact]
    public void Missing_weight_property_falls_back_to_default_weight()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        // 重みプロパティを付けないエッジは既定重み 1.0 として扱われる。
        tx.CreateRelationship(a, b, "K");
        tx.CreateRelationship(b, c, "K");

        var (dist, nodes, _) = RunOperator(tx, a, c);

        dist.Should().Be(2.0);          // 1.0 + 1.0
        nodes.Should().Equal(a, b, c);
        tx.Rollback();
    }

    [Fact]
    public void TypeFilter_restricts_traversal_to_requested_relationship_type()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        // ROAD 経由は重み 5、RAIL 経由は重み 1 — RAIL に限定すると ROAD は使えない。
        SetWeight(tx, tx.CreateRelationship(a, b, "ROAD"), 1.0);
        SetWeight(tx, tx.CreateRelationship(a, b, "RAIL"), 5.0);

        var keyId = _db.Schema.GetOrCreatePropertyKey("w");
        var railType = _db.Schema.GetOrCreateRelationshipType("RAIL");
        using var op = new WeightedShortestPathOperator(
            new PairSource(a, b), 0, 1, Direction.Outgoing, railType,
            new PropertyChainWeightProvider(keyId));
        using var result = tx.Execute(op);

        var row = result.Rows().Single();
        row.GetDouble(2).Should().Be(5.0);   // RAIL のみ — 重い方を強制
        tx.Rollback();
    }

    [Fact]
    public void AStar_with_admissible_heuristic_matches_dijkstra_and_expands_no_more_nodes()
    {
        using var tx = _db.BeginTransaction();
        // 線形チェーン n0 -> n1 -> ... -> n5 (各重み 1)、分岐の袋小路 n1 -> dead。
        var n = new NodeId[6];
        for (int i = 0; i < 6; i++) n[i] = tx.CreateNode("X");
        for (int i = 0; i < 5; i++)
            SetWeight(tx, tx.CreateRelationship(n[i], n[i + 1], "K"), 1.0);
        var dead = tx.CreateNode("X");
        SetWeight(tx, tx.CreateRelationship(n[1], dead, "K"), 1.0);

        var keyId = _db.Schema.GetOrCreatePropertyKey("w");

        // Dijkstra (heuristic 無)。
        using var dij = new WeightedShortestPathOperator(
            new PairSource(n[0], n[5]), 0, 1, Direction.Outgoing, null,
            new PropertyChainWeightProvider(keyId));
        double dijDist;
        using (var r = tx.Execute(dij)) dijDist = r.Rows().Single().GetDouble(2);

        // A* — 残ホップ数を正確に返す consistent ヒューリスティック。
        var remain = new Dictionary<long, double>();
        for (int i = 0; i < 6; i++) remain[n[i].Value] = 5 - i;
        remain[dead.Value] = 100;   // 袋小路は終点から遠いと見積もる
        using var astar = new WeightedShortestPathOperator(
            new PairSource(n[0], n[5]), 0, 1, Direction.Outgoing, null,
            new PropertyChainWeightProvider(keyId),
            heuristic: node => remain.GetValueOrDefault(node.Value, 0.0));
        double astarDist;
        using (var r = tx.Execute(astar)) astarDist = r.Rows().Single().GetDouble(2);

        astarDist.Should().Be(dijDist).And.Be(5.0);
        astar.ExpandedNodeCount.Should().BeLessThanOrEqualTo(dij.ExpandedNodeCount);
        tx.Rollback();
    }

    // ------------------------------------------------------------- client API --

    [Fact]
    public void Client_WeightedShortestPath_returns_distance_and_path()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        SetWeight(tx, tx.CreateRelationship(a, b, "K"), 10.0);
        var ac = tx.CreateRelationship(a, c, "K"); SetWeight(tx, ac, 3.0);
        var cb = tx.CreateRelationship(c, b, "K"); SetWeight(tx, cb, 4.0);

        var g = tx.G(_db.Schema);
        var result = g.WeightedShortestPath(a, b, "w");

        result.Found.Should().BeTrue();
        result.Distance.Should().Be(7.0);
        result.Nodes.Should().Equal(a, c, b);
        result.Relationships.Should().Equal(ac, cb);
        tx.Rollback();
    }

    [Fact]
    public void Client_WeightedShortestPath_returns_NotFound_when_unreachable()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");

        var g = tx.G(_db.Schema);
        var result = g.WeightedShortestPath(a, b, "w");

        result.Found.Should().BeFalse();
        result.Distance.Should().Be(double.PositiveInfinity);
        result.Nodes.Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Client_WeightedShortestPathAStar_grid_matches_dijkstra()
    {
        using var tx = _db.BeginTransaction();
        const int n = 6;
        var grid = BuildWeightedGrid(tx, n);
        var src = grid[0, 0];
        var dst = grid[n - 1, n - 1];

        var g = tx.G(_db.Schema);
        var dijkstra = g.WeightedShortestPath(src, dst, "w");
        var astar = g.WeightedShortestPathAStar(src, dst, "w", "x", "y", HeuristicMetric.Euclidean);

        astar.Found.Should().BeTrue();
        astar.Distance.Should().BeApproximately(dijkstra.Distance, 1e-9);
        astar.Distance.Should().Be(2.0 * (n - 1));   // 各エッジ重み 1、最短はマンハッタン距離
        astar.Nodes[0].Should().Be(src);
        astar.Nodes[^1].Should().Be(dst);
        astar.Relationships.Should().HaveCount(astar.Nodes.Count - 1);
        tx.Rollback();
    }

    [Fact]
    public void Client_WeightedShortestPath_maxDistance_prunes_long_paths()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        SetWeight(tx, tx.CreateRelationship(a, b, "K"), 5.0);
        SetWeight(tx, tx.CreateRelationship(b, c, "K"), 5.0);

        var g = tx.G(_db.Schema);
        g.WeightedShortestPath(a, c, "w", maxDistance: 9.0).Found.Should().BeFalse();
        g.WeightedShortestPath(a, c, "w", maxDistance: 10.0).Found.Should().BeTrue();
        tx.Rollback();
    }

    [Fact]
    public void WeightedPathCodec_round_trips_nodes_and_relationships()
    {
        NodeId[] nodes = [new(7), new(11), new(13)];
        RelationshipId[] rels = [new(100), new(200)];

        var bytes = WeightedPathCodec.Encode(nodes, rels);
        WeightedPathCodec.Decode(bytes, out var dn, out var dr);

        dn.Should().Equal(nodes);
        dr.Should().Equal(rels);
    }

    // ------------------------------------------------------------------ helpers --

    private void SetWeight(IGraphTransaction tx, RelationshipId rel, double weight)
        => tx.SetProperty(rel, "w", PropertyValue.FromDouble(weight));

    private (double Distance, NodeId[] Nodes, RelationshipId[] Rels) RunOperator(
        IGraphTransaction tx, NodeId source, NodeId target)
    {
        var keyId = _db.Schema.GetOrCreatePropertyKey("w");
        using var op = new WeightedShortestPathOperator(
            new PairSource(source, target), 0, 1, Direction.Outgoing, null,
            new PropertyChainWeightProvider(keyId));
        using var result = tx.Execute(op);
        var row = result.Rows().Single();
        WeightedPathCodec.Decode(row.GetBytes(3), out var nodes, out var rels);
        return (row.GetDouble(2), nodes, rels);
    }

    /// <summary>n×n 格子グラフを構築する。各ノードは x/y 座標、各エッジは重み 1.0。</summary>
    private NodeId[,] BuildWeightedGrid(IGraphTransaction tx, int n)
    {
        var grid = new NodeId[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                var node = tx.CreateNode("Cell");
                tx.SetProperty(node, "x", PropertyValue.FromDouble(c));
                tx.SetProperty(node, "y", PropertyValue.FromDouble(r));
                grid[r, c] = node;
            }
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                if (c + 1 < n) SetWeight(tx, tx.CreateRelationship(grid[r, c], grid[r, c + 1], "K"), 1.0);
                if (r + 1 < n) SetWeight(tx, tx.CreateRelationship(grid[r, c], grid[r + 1, c], "K"), 1.0);
            }
        return grid;
    }

    /// <summary>単一 (source, target) ペアを 1 行だけ放出する source operator。</summary>
    private sealed class PairSource(NodeId src, NodeId tgt) : IPhysicalOperator
    {
        private readonly TupleSlot[] _buffer = new TupleSlot[2];
        private bool _emitted;

        public TupleSchema Schema { get; } = new([
            new ColumnDefinition("s", TupleSlotType.NodeId),
            new ColumnDefinition("t", TupleSlotType.NodeId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);

        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }

        public bool MoveNext()
        {
            if (_emitted) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = src.Value };
            _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = tgt.Value };
            _emitted = true;
            return true;
        }

        public void Dispose() { }
    }
}

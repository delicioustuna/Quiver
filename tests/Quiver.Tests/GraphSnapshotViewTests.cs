using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// CSR / CSC スナップショットビューを検証する。
/// 具現化した隣接スパンの正しさ、現在のトランザクション状態との同値性、
/// ビューを利用する PageRank カーネルを対象とする。
/// </summary>
public sealed class GraphSnapshotViewTests : IDisposable
{
    private readonly string _dir;
    private GraphDatabase? _db;

    public GraphSnapshotViewTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_pw15_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Snapshot_exposes_csr_and_csc_for_bulk_loaded_graph()
    {
        // Build a small DAG: 0→1, 0→2, 1→3, 2→3.
        BulkLoad(nodeCount: 4, edges: new[] { (0L, 1L), (0L, 2L), (1L, 3L), (2L, 3L) });

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var view = _db.OpenSnapshotView();

        view.NodeCount.Should().Be(4);
        view.EdgeCount.Should().Be(4);
        view.Epoch.Should().Be(1, "bulk-load with adjacency index initialises epoch=1");

        view.OutDegree(new NodeId(0)).Should().Be(2);
        view.OutDegree(new NodeId(3)).Should().Be(0);
        view.InDegree(new NodeId(3)).Should().Be(2);
        view.InDegree(new NodeId(0)).Should().Be(0);

        view.OutNeighbors(new NodeId(0)).ToArray().Should().BeEquivalentTo(new[] { 1L, 2L });
        view.InNeighbors(new NodeId(3)).ToArray().Should().BeEquivalentTo(new[] { 1L, 2L });
        view.OutRelationshipIds(new NodeId(0)).Length.Should().Be(2);
    }

    [Fact]
    public void Snapshot_includes_isolated_nodes_with_no_edges()
    {
        BulkLoad(nodeCount: 5, edges: new[] { (0L, 1L) });

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var view = _db.OpenSnapshotView();

        view.NodeCount.Should().Be(5);
        for (long n = 2; n < 5; n++)
        {
            view.OutDegree(new NodeId(n)).Should().Be(0);
            view.InDegree(new NodeId(n)).Should().Be(0);
            view.OutNeighbors(new NodeId(n)).IsEmpty.Should().BeTrue();
        }
    }

    [Fact]
    public void Snapshot_out_of_range_nodeid_returns_empty()
    {
        BulkLoad(nodeCount: 2, edges: new[] { (0L, 1L) });

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var view = _db.OpenSnapshotView();

        view.OutDegree(new NodeId(99)).Should().Be(0);
        view.OutNeighbors(new NodeId(99)).IsEmpty.Should().BeTrue();
        view.InNeighbors(new NodeId(-1)).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Snapshot_picks_up_delta_edges_created_after_bulk_load()
    {
        BulkLoad(nodeCount: 3, edges: new[] { (0L, 1L) });

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = _db.BeginTransaction())
        {
            tx.CreateRelationship(new NodeId(0), new NodeId(2), "R");
            tx.Commit();
        }

        using var view = _db.OpenSnapshotView();
        view.OutNeighbors(new NodeId(0)).ToArray().Should().BeEquivalentTo(new[] { 1L, 2L });
        view.EdgeCount.Should().Be(2);
    }

    [Fact]
    public void Snapshot_is_point_in_time_and_does_not_observe_post_build_edits()
    {
        BulkLoad(nodeCount: 3, edges: new[] { (0L, 1L) });

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var view = _db.OpenSnapshotView();
        long edgesBefore = view.EdgeCount;

        // Mutate after view construction — view should still see the old shape.
        using (var tx = _db.BeginTransaction())
        {
            tx.CreateRelationship(new NodeId(1), new NodeId(2), "R");
            tx.Commit();
        }

        view.EdgeCount.Should().Be(edgesBefore);
        view.OutDegree(new NodeId(1)).Should().Be(0, "post-build edge is not visible");
    }

    [Fact]
    public void HasWeights_is_false_when_v2_payload_lane_is_absent()
    {
        BulkLoad(nodeCount: 2, edges: new[] { (0L, 1L) });

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var view = _db.OpenSnapshotView();
        view.HasWeights.Should().BeFalse();
        view.WeightBitsOut(new NodeId(0)).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Dispose_returns_arrays_to_pool_safely_when_called_twice()
    {
        BulkLoad(nodeCount: 2, edges: new[] { (0L, 1L) });
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var view = _db.OpenSnapshotView();
        view.Dispose();
        Action act = view.Dispose;
        act.Should().NotThrow();
    }

    // ────────────────────── PageRank kernel ──────────────────────

    [Fact]
    public void PageRank_on_two_node_chain_concentrates_rank_on_sink()
    {
        // 0 -> 1 means rank flows from 0 into 1; with damping 0.85 and dangling
        // node 1 redistributing uniformly, rank[1] ends up larger than rank[0].
        BulkLoad(nodeCount: 2, edges: new[] { (0L, 1L) });

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var view = _db.OpenSnapshotView();
        var ranks = GraphAlgorithms.PageRank(view, damping: 0.85, iterations: 50);

        ranks.Should().HaveCount(2);
        double sum = ranks.Sum();
        sum.Should().BeApproximately(1.0, 1e-9, "PageRank preserves total mass");
        ranks[1].Should().BeGreaterThan(ranks[0]);
    }

    [Fact]
    public void PageRank_on_uniform_cycle_converges_to_uniform_distribution()
    {
        // Symmetric cycle 0→1→2→0 — every node has identical structural role.
        BulkLoad(nodeCount: 3, edges: new[] { (0L, 1L), (1L, 2L), (2L, 0L) });

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var view = _db.OpenSnapshotView();
        var ranks = GraphAlgorithms.PageRank(view, damping: 0.85, iterations: 100);

        ranks.Should().HaveCount(3);
        ranks[0].Should().BeApproximately(1.0 / 3.0, 1e-6);
        ranks[1].Should().BeApproximately(1.0 / 3.0, 1e-6);
        ranks[2].Should().BeApproximately(1.0 / 3.0, 1e-6);
    }

    [Fact]
    public void PageRank_handles_empty_graph()
    {
        BulkLoad(nodeCount: 0, edges: Array.Empty<(long, long)>());
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var view = _db.OpenSnapshotView();
        var ranks = GraphAlgorithms.PageRank(view);
        ranks.Should().BeEmpty();
    }

    // ─────────────────────────── helpers ───────────────────────────

    private void BulkLoad(int nodeCount, (long Src, long Tgt)[] edges)
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        for (int i = 0; i < nodeCount; i++)
            loader.AppendNode(new NodeId(i), new LabelId(0));
        for (int i = 0; i < edges.Length; i++)
            loader.AppendRelationship(
                new RelationshipId(i),
                new NodeId(edges[i].Src), new NodeId(edges[i].Tgt),
                new RelationshipTypeId(0));
        loader.Commit();
    }
}

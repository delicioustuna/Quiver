using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// PW-14 / codex_advice_3 §7.6: immutable base view + mutable delta. Covers
/// the merge cursor, tombstone filtering, and compact rebuild.
///
/// The tests use the binary backend with <c>BeginBulkLoad(buildAdjacencyIndex:
/// true)</c> to materialise a base view, then exercise post-bulk-load creates
/// and deletes that exercise the delta path.
/// </summary>
public sealed class AdjacencyEpochTests : IDisposable
{
    private readonly string _dir;
    private GraphDatabase? _db;

    public AdjacencyEpochTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_pw14_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ────────────────────────────── core merge ──────────────────────────────

    [Fact]
    public void Bulk_loaded_base_alone_returns_only_base_edges()
    {
        BulkLoad(nodeCount: 3, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = GraphDatabase.Open(_dir);
        using var tx = _db.BeginTransaction();
        var neighbors = ExpandOut(tx, new NodeId(0));
        neighbors.Should().BeEquivalentTo(new[] { 1L, 2L });
        tx.AdjacencyBlocks!.BaseRelHwm.Should().Be(2,
            "BulkLoader wrote 2 rels so the watermark sits at id 2");
        tx.AdjacencyBlocks.Epoch.Should().Be(1);
    }

    [Fact]
    public void Delta_rel_created_after_bulk_load_is_visible_without_duplicating_base()
    {
        BulkLoad(nodeCount: 4, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = GraphDatabase.Open(_dir);
        // node 3 was reserved at bulk-load (4 nodes) but had no edges; add a
        // new delta edge 0→3. The new rel gets id >= BaseRelHwm so the merge
        // must yield {1, 2, 3} with no double-emission of 1 or 2.
        using (var tx = _db.BeginTransaction())
        {
            tx.CreateRelationship(new NodeId(0), new NodeId(3), "R");
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            var neighbors = ExpandOut(tx, new NodeId(0));
            neighbors.Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
        }
    }

    [Fact]
    public void Tombstone_skips_deleted_base_edge_without_rebuild()
    {
        BulkLoad(nodeCount: 3, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = GraphDatabase.Open(_dir);
        using (var tx = _db.BeginTransaction())
        {
            // Delete the rel pointing 0→1 (id 0 by bulk-load order).
            tx.DeleteRelationship(new RelationshipId(0));
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            var neighbors = ExpandOut(tx, new NodeId(0));
            neighbors.Should().BeEquivalentTo(new[] { 2L });
            tx.AdjacencyBlocks!.IsTombstoned(new RelationshipId(0)).Should().BeTrue();
        }
    }

    [Fact]
    public void Mixed_base_plus_delta_with_base_delete_and_delta_delete()
    {
        BulkLoad(nodeCount: 5, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = GraphDatabase.Open(_dir);
        long deltaRelId;
        using (var tx = _db.BeginTransaction())
        {
            // Add two deltas.
            tx.CreateRelationship(new NodeId(0), new NodeId(3), "R"); // first delta
            var second = tx.CreateRelationship(new NodeId(0), new NodeId(4), "R");
            deltaRelId = second.Value;
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            // Delete one base edge (0→1) and one delta edge (0→4).
            tx.DeleteRelationship(new RelationshipId(0));
            tx.DeleteRelationship(new RelationshipId(deltaRelId));
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            var neighbors = ExpandOut(tx, new NodeId(0));
            neighbors.Should().BeEquivalentTo(new[] { 2L, 3L });
        }
    }

    [Fact]
    public void Tombstones_persist_across_reopen()
    {
        BulkLoad(nodeCount: 3, edges: new[] { (0L, 1L), (0L, 2L) });

        using (var db = GraphDatabase.Open(_dir))
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteRelationship(new RelationshipId(0));
            tx.Commit();
        }
        _db = GraphDatabase.Open(_dir);
        using var tx2 = _db.BeginTransaction();
        tx2.AdjacencyBlocks!.IsTombstoned(new RelationshipId(0)).Should().BeTrue();
        var neighbors = ExpandOut(tx2, new NodeId(0));
        neighbors.Should().BeEquivalentTo(new[] { 2L });
    }

    // ────────────────────────────── compact ──────────────────────────────

    [Fact]
    public void Compact_absorbs_deltas_into_base_and_advances_epoch()
    {
        BulkLoad(nodeCount: 5, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = GraphDatabase.Open(_dir);
        using (var tx = _db.BeginTransaction())
        {
            tx.CreateRelationship(new NodeId(0), new NodeId(3), "R");
            tx.CreateRelationship(new NodeId(0), new NodeId(4), "R");
            tx.Commit();
        }

        long epochBefore;
        using (var tx = _db.BeginTransaction())
        {
            epochBefore = tx.AdjacencyBlocks!.Epoch;
        }

        _db.CompactAdjacency();

        using var txAfter = _db.BeginTransaction();
        txAfter.AdjacencyBlocks!.Epoch.Should().Be(epochBefore + 1);
        // After compact, BaseRelHwm must cover every live rel id — there are
        // 4 ids in [0..3] so hwm = 4.
        txAfter.AdjacencyBlocks.BaseRelHwm.Should().Be(4);
        ExpandOut(txAfter, new NodeId(0))
            .Should().BeEquivalentTo(new[] { 1L, 2L, 3L, 4L });
    }

    [Fact]
    public void Compact_drops_tombstones_and_excludes_deleted_base_edges()
    {
        BulkLoad(nodeCount: 4, edges: new[] { (0L, 1L), (0L, 2L), (0L, 3L) });

        _db = GraphDatabase.Open(_dir);
        using (var tx = _db.BeginTransaction())
        {
            tx.DeleteRelationship(new RelationshipId(1)); // base edge 0→2
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            tx.AdjacencyBlocks!.IsTombstoned(new RelationshipId(1)).Should().BeTrue();
        }

        _db.CompactAdjacency();

        using var tx2 = _db.BeginTransaction();
        // After compact the deleted edge is physically gone, so the tombstone
        // for the *new* base has nothing to do — IsTombstoned should report false.
        tx2.AdjacencyBlocks!.IsTombstoned(new RelationshipId(1)).Should().BeFalse();
        ExpandOut(tx2, new NodeId(0)).Should().BeEquivalentTo(new[] { 1L, 3L });
    }

    [Fact]
    public void Compact_throws_when_a_transaction_is_active()
    {
        BulkLoad(nodeCount: 2, edges: new[] { (0L, 1L) });

        _db = GraphDatabase.Open(_dir);
        using var tx = _db.BeginTransaction();
        Action act = () => _db.CompactAdjacency();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*active transactions*");
    }

    // ────────────────────── AdjacencyEpoch unit-level ────────────────────────

    [Fact]
    public void AdjacencyEpoch_round_trip_preserves_tombstones_and_epoch()
    {
        var path = Path.Combine(_dir, "epoch.dat");
        Directory.CreateDirectory(_dir);

        var e = AdjacencyEpoch.CreateNew(path, baseRelHwm: 100);
        e.Tombstone(5);
        e.Tombstone(42);
        e.Tombstone(42); // duplicate; should stay at 2

        var reloaded = AdjacencyEpoch.Load(path);
        reloaded.Epoch.Should().Be(1);
        reloaded.BaseRelHwm.Should().Be(100);
        reloaded.TombstoneCount.Should().Be(2);
        reloaded.IsTombstoned(5).Should().BeTrue();
        reloaded.IsTombstoned(42).Should().BeTrue();
        reloaded.IsTombstoned(6).Should().BeFalse();
    }

    [Fact]
    public void AdjacencyEpoch_tombstone_outside_base_range_is_noop()
    {
        var path = Path.Combine(_dir, "epoch.dat");
        Directory.CreateDirectory(_dir);

        var e = AdjacencyEpoch.CreateNew(path, baseRelHwm: 10);
        e.Tombstone(15); // outside base — ignored
        e.TombstoneCount.Should().Be(0);
        e.IsTombstoned(15).Should().BeFalse();
    }

    [Fact]
    public void AdjacencyEpoch_reset_after_compact_clears_tombstones_and_bumps_epoch()
    {
        var path = Path.Combine(_dir, "epoch.dat");
        Directory.CreateDirectory(_dir);

        var e = AdjacencyEpoch.CreateNew(path, baseRelHwm: 50);
        e.Tombstone(3);
        e.Tombstone(7);
        e.ResetAfterCompact(newBaseRelHwm: 200);

        e.Epoch.Should().Be(2);
        e.BaseRelHwm.Should().Be(200);
        e.TombstoneCount.Should().Be(0);

        // Persisted state matches in-memory state.
        var reloaded = AdjacencyEpoch.Load(path);
        reloaded.Epoch.Should().Be(2);
        reloaded.BaseRelHwm.Should().Be(200);
        reloaded.TombstoneCount.Should().Be(0);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private void BulkLoad(int nodeCount, (long Src, long Tgt)[] edges)
    {
        using var db = GraphDatabase.Open(_dir);
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

    private static List<long> ExpandOut(IGraphTransaction tx, NodeId source)
    {
        // Going through Execute(ExpandOperator(...)) exercises the binary
        // backend's merged expand cursor (base via adjacency block, then delta
        // via linked list), which is exactly the path PW-14 changed.
        var op = new ExpandOperator(
            new SingleNodeSource(source),
            sourceNodeColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborOnly);
        var result = tx.Execute(op);
        return result.Rows().Select(r => r.GetNodeId(0).Value).ToList();
    }

    private sealed class SingleNodeSource : IPhysicalOperator
    {
        private readonly TupleSlot[] _buf = new TupleSlot[1];
        private readonly NodeId _node;
        private bool _emitted;

        public SingleNodeSource(NodeId node) { _node = node; }
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.NodeId)]);
        public OperatorStatistics Statistics { get; private set; }
        public TupleRef Current => new(_buf);

        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }

        public bool MoveNext()
        {
            if (_emitted) return false;
            _buf[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _node.Value };
            _emitted = true;
            return true;
        }

        public void Dispose() { }
    }
}

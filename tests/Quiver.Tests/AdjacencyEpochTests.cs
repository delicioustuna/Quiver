using Quiver;
using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 不変の基底ビューと可変差分からなる隣接エポックについて、
/// マージカーソル、墓石の除外、圧縮再構築を検証する。
///
/// バイナリバックエンドで <c>BeginBulkLoad(buildAdjacencyIndex: true)</c> を使って基底ビューを作り、
/// 一括読み込み後の作成と削除によって差分経路を実行する。
/// </summary>
public sealed class AdjacencyEpochTests : IDisposable
{
    private readonly string _dir;
    private QuiverDatabase? _db;

    public AdjacencyEpochTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_pw14_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _db?.Dispose();
        foreach (var c in _containers) c.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ────────────────────────────── core merge ──────────────────────────────

    [Fact]
    public void Bulk_loaded_base_alone_returns_only_base_edges()
    {
        BulkLoad(vertexCount: 3, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var tx = _db.BeginTransaction();
        var neighbors = ExpandOut(tx, new VertexId(0));
        neighbors.Should().BeEquivalentTo(new[] { 1L, 2L });
        tx.AsInternal().AdjacencySegments!.BaseEdgeHwm.Should().Be(2,
            "BulkLoader wrote 2 edges so the watermark sits at id 2");
        tx.AsInternal().AdjacencySegments!.Epoch.Should().Be(1);
    }

    [Fact]
    public void Delta_edge_created_after_bulk_load_is_visible_without_duplicating_base()
    {
        BulkLoad(vertexCount: 4, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        // vertex 3 was reserved at bulk-load (4 vertices) but had no edges; add a
        // new delta edge 0→3. The new edge gets id >= BaseEdgeHwm so the merge
        // must yield {1, 2, 3} with no double-emission of 1 or 2.
        using (var tx = _db.BeginTransaction())
        {
            tx.CreateEdge(new VertexId(0), new VertexId(3), "R");
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            var neighbors = ExpandOut(tx, new VertexId(0));
            neighbors.Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
        }
    }

    [Fact]
    public void Tombstone_skips_deleted_base_edge_without_rebuild()
    {
        BulkLoad(vertexCount: 3, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = _db.BeginTransaction())
        {
            // Delete the edge pointing 0→1 (id 0 by bulk-load order).
            tx.DeleteEdge(new EdgeId(0));
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            var neighbors = ExpandOut(tx, new VertexId(0));
            neighbors.Should().BeEquivalentTo(new[] { 2L });
            tx.AsInternal().AdjacencySegments!.IsTombstoned(new EdgeId(0)).Should().BeTrue();
        }
    }

    [Fact]
    public void Mixed_base_plus_delta_with_base_delete_and_delta_delete()
    {
        BulkLoad(vertexCount: 5, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        long deltaEdgeId;
        using (var tx = _db.BeginTransaction())
        {
            // Add two deltas.
            tx.CreateEdge(new VertexId(0), new VertexId(3), "R"); // first delta
            var second = tx.CreateEdge(new VertexId(0), new VertexId(4), "R");
            deltaEdgeId = second.Value;
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            // Delete one base edge (0→1) and one delta edge (0→4).
            tx.DeleteEdge(new EdgeId(0));
            tx.DeleteEdge(new EdgeId(deltaEdgeId));
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            var neighbors = ExpandOut(tx, new VertexId(0));
            neighbors.Should().BeEquivalentTo(new[] { 2L, 3L });
        }
    }

    [Fact]
    public void Read_only_snapshot_keeps_base_edge_deleted_after_begin()
    {
        BulkLoad(vertexCount: 3, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var reader = _db.BeginReadOnlyTransaction();

        using (var writer = _db.BeginTransaction())
        {
            writer.DeleteEdge(new EdgeId(0));
            writer.Commit();
        }

        ExpandOut(reader, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 2L });

        using var nextReader = _db.BeginReadOnlyTransaction();
        ExpandOut(nextReader, new VertexId(0)).Should().BeEquivalentTo(new[] { 2L });
    }

    [Fact]
    public void Read_only_snapshot_does_not_see_delta_insert_committed_after_begin()
    {
        BulkLoad(vertexCount: 4, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var reader = _db.BeginReadOnlyTransaction();

        using (var writer = _db.BeginTransaction())
        {
            writer.CreateEdge(new VertexId(0), new VertexId(3), "R");
            writer.Commit();
        }

        ExpandOut(reader, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 2L });

        using var nextReader = _db.BeginReadOnlyTransaction();
        ExpandOut(nextReader, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
    }

    [Fact]
    public void Read_only_snapshot_keeps_existing_delta_when_new_delta_becomes_head()
    {
        BulkLoad(vertexCount: 5, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var writer = _db.BeginTransaction())
        {
            writer.CreateEdge(new VertexId(0), new VertexId(3), "R");
            writer.Commit();
        }

        using var reader = _db.BeginReadOnlyTransaction();
        using (var writer = _db.BeginTransaction())
        {
            writer.CreateEdge(new VertexId(0), new VertexId(4), "R");
            writer.Commit();
        }

        ExpandOut(reader, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 2L, 3L });

        using var nextReader = _db.BeginReadOnlyTransaction();
        ExpandOut(nextReader, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 2L, 3L, 4L });
    }

    [Fact]
    public void Read_only_snapshot_keeps_delta_edge_deleted_after_begin()
    {
        BulkLoad(vertexCount: 4, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        EdgeId delta;
        using (var writer = _db.BeginTransaction())
        {
            delta = writer.CreateEdge(new VertexId(0), new VertexId(3), "R");
            writer.Commit();
        }

        using var reader = _db.BeginReadOnlyTransaction();
        using (var writer = _db.BeginTransaction())
        {
            writer.DeleteEdge(delta);
            writer.Commit();
        }

        ExpandOut(reader, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 2L, 3L });

        using var nextReader = _db.BeginReadOnlyTransaction();
        ExpandOut(nextReader, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 2L });
    }

    [Fact]
    public void Read_only_snapshot_keeps_edge_property_updated_after_begin()
    {
        BulkLoad(vertexCount: 2, edges: new[] { (0L, 1L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var writer = _db.BeginTransaction())
        {
            writer.SetProperty(new EdgeId(0), "weight", PropertyValue.FromInt64(10));
            writer.Commit();
        }

        using var reader = _db.BeginReadOnlyTransaction();
        using (var writer = _db.BeginTransaction())
        {
            writer.SetProperty(new EdgeId(0), "weight", PropertyValue.FromInt64(20));
            writer.Commit();
        }

        reader.GetProperty(new EdgeId(0), "weight").Int64Value.Should().Be(10);

        using var nextReader = _db.BeginReadOnlyTransaction();
        nextReader.GetProperty(new EdgeId(0), "weight").Int64Value.Should().Be(20);
    }

    [Fact]
    public void Tombstones_persist_across_reopen()
    {
        BulkLoad(vertexCount: 3, edges: new[] { (0L, 1L), (0L, 2L) });

        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteEdge(new EdgeId(0));
            tx.Commit();
        }
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var tx2 = _db.BeginTransaction();
        tx2.AsInternal().AdjacencySegments!.IsTombstoned(new EdgeId(0)).Should().BeTrue();
        var neighbors = ExpandOut(tx2, new VertexId(0));
        neighbors.Should().BeEquivalentTo(new[] { 2L });
    }

    // ────────────────────────────── compact ──────────────────────────────

    [Fact]
    public void Compact_absorbs_deltas_into_base_and_advances_epoch()
    {
        BulkLoad(vertexCount: 5, edges: new[] { (0L, 1L), (0L, 2L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = _db.BeginTransaction())
        {
            tx.CreateEdge(new VertexId(0), new VertexId(3), "R");
            tx.CreateEdge(new VertexId(0), new VertexId(4), "R");
            tx.Commit();
        }

        long epochBefore;
        using (var tx = _db.BeginTransaction())
        {
            epochBefore = tx.AsInternal().AdjacencySegments!.Epoch;
        }

        _db.CompactAdjacency();

        using var txAfter = _db.BeginTransaction();
        txAfter.AsInternal().AdjacencySegments!.Epoch.Should().Be(epochBefore + 1);
        // After compact, BaseEdgeHwm must cover every live edge id — there are
        // 4 ids in [0..3] so hwm = 4.
        txAfter.AsInternal().AdjacencySegments!.BaseEdgeHwm.Should().Be(4);
        ExpandOut(txAfter, new VertexId(0))
            .Should().BeEquivalentTo(new[] { 1L, 2L, 3L, 4L });
    }

    [Fact]
    public void Compact_drops_tombstones_and_excludes_deleted_base_edges()
    {
        BulkLoad(vertexCount: 4, edges: new[] { (0L, 1L), (0L, 2L), (0L, 3L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = _db.BeginTransaction())
        {
            tx.DeleteEdge(new EdgeId(1)); // base edge 0→2
            tx.Commit();
        }
        using (var tx = _db.BeginTransaction())
        {
            tx.AsInternal().AdjacencySegments!.IsTombstoned(new EdgeId(1)).Should().BeTrue();
        }

        _db.CompactAdjacency();

        using var tx2 = _db.BeginTransaction();
        // After compact the deleted edge is physically gone, so the tombstone
        // for the *new* base has nothing to do — IsTombstoned should report false.
        tx2.AsInternal().AdjacencySegments!.IsTombstoned(new EdgeId(1)).Should().BeFalse();
        ExpandOut(tx2, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 3L });
    }

    [Fact]
    public void Compact_throws_when_a_transaction_is_active()
    {
        BulkLoad(vertexCount: 2, edges: new[] { (0L, 1L) });

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var tx = _db.BeginTransaction();
        Action act = () => _db.CompactAdjacency();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*active transactions*");
    }

    // ────────────────────── AdjacencyEpoch unit-level ────────────────────────

    // エポックは専用テナント (IPagedFile) に保存される。同じコンテナ上で
    // Open し直すと、ページに書かれた内容 (buffer pool 経由) からメタを再構築できる。
    private Quiver.Storage.SingleFileContainer NewContainer()
    {
        Directory.CreateDirectory(_dir);
        var c = new Quiver.Storage.SingleFileContainer(
            Path.Combine(_dir, "epoch_" + Guid.NewGuid().ToString("N")[..8] + ".quiver"));
        _containers.Add(c);
        return c;
    }
    private readonly List<Quiver.Storage.SingleFileContainer> _containers = new();

    [Fact]
    public void AdjacencyEpoch_round_trip_preserves_tombstones_and_epoch()
    {
        var container = NewContainer();
        var tenant = container.OpenTenant(AdjacencyContainer.EpochTenant, Quiver.Storage.PageKind.Header);

        var e = AdjacencyEpoch.CreateNew(tenant, baseEdgeHwm: 100);
        e.Tombstone(5);
        e.Tombstone(42);
        e.Tombstone(42); // duplicate; should stay at 2

        var reloaded = AdjacencyEpoch.Open(tenant);
        reloaded.Epoch.Should().Be(1);
        reloaded.BaseEdgeHwm.Should().Be(100);
        reloaded.TombstoneCount.Should().Be(2);
        reloaded.IsTombstoned(5).Should().BeTrue();
        reloaded.IsTombstoned(42).Should().BeTrue();
        reloaded.IsTombstoned(6).Should().BeFalse();
    }

    [Fact]
    public void AdjacencyEpoch_tombstone_outside_base_range_is_noop()
    {
        var container = NewContainer();
        var tenant = container.OpenTenant(AdjacencyContainer.EpochTenant, Quiver.Storage.PageKind.Header);

        var e = AdjacencyEpoch.CreateNew(tenant, baseEdgeHwm: 10);
        e.Tombstone(15); // outside base — ignored
        e.TombstoneCount.Should().Be(0);
        e.IsTombstoned(15).Should().BeFalse();
    }

    [Fact]
    public void AdjacencyEpoch_reset_after_compact_clears_tombstones_and_bumps_epoch()
    {
        var container = NewContainer();
        var tenant = container.OpenTenant(AdjacencyContainer.EpochTenant, Quiver.Storage.PageKind.Header);

        var e = AdjacencyEpoch.CreateNew(tenant, baseEdgeHwm: 50);
        e.Tombstone(3);
        e.Tombstone(7);
        e.ResetAfterCompact(newBaseEdgeHwm: 200);

        e.Epoch.Should().Be(2);
        e.BaseEdgeHwm.Should().Be(200);
        e.TombstoneCount.Should().Be(0);

        // Persisted state matches in-memory state.
        var reloaded = AdjacencyEpoch.Open(tenant);
        reloaded.Epoch.Should().Be(2);
        reloaded.BaseEdgeHwm.Should().Be(200);
        reloaded.TombstoneCount.Should().Be(0);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private void BulkLoad(int vertexCount, (long Src, long Tgt)[] edges)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        for (int i = 0; i < vertexCount; i++)
            loader.AppendVertex(new VertexId(i), new LabelId(0));
        for (int i = 0; i < edges.Length; i++)
            loader.AppendEdge(
                new EdgeId(i),
                new VertexId(edges[i].Src), new VertexId(edges[i].Tgt),
                new EdgeTypeId(0));
        loader.Commit();
    }

    private static List<long> ExpandOut(IGraphTransaction tx, VertexId source)
    {
        // Going through Execute(ExpandOperator(...)) exercises the binary
        // backend's merged expand cursor (base via adjacency block, then delta
        // 連結リスト経由で辿り、差分マージの対象経路を通す。
        var op = new ExpandOperator(
            new SingleVertexSource(source),
            sourceVertexColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborOnly);
        var result = tx.Execute(op);
        // 隣接のスロット番号 (Sequence) を検証する。Value は世代を含む。
        return result.Rows().Select(r => r.GetVertexId(0).Sequence).ToList();
    }

    private sealed class SingleVertexSource : IPhysicalOperator
    {
        private readonly TupleSlot[] _buf = new TupleSlot[1];
        private readonly VertexId _vertex;
        private bool _emitted;

        public SingleVertexSource(VertexId vertex) { _vertex = vertex; }
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.VertexId)]);
        public OperatorStatistics Statistics { get; private set; }
        public TupleRef Current => new(_buf);

        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }

        public bool MoveNext()
        {
            if (_emitted) return false;
            _buf[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _vertex.Value };
            _emitted = true;
            return true;
        }

        public void Dispose() { }
    }
}

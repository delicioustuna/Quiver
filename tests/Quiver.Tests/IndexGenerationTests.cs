using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ARCH-3: インデックス値レーンへの Generation 導入。slot の物理再利用 (vacuum free list) に伴う
/// stale 索引エントリ (ABA) が別ノードを誤って返さないこと、orphan GC が世代不一致を回収できること、
/// フォーマットバージョンが V4 に進み旧フォーマットを拒否することを検証する。
/// </summary>
public sealed class IndexGenerationTests : IDisposable
{
    private readonly string _dir;

    public IndexGenerationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_arch3_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- GenerationalRef pack / unpack ----

    [Theory]
    [InlineData(EntityKind.Node, 0L, 0)]
    [InlineData(EntityKind.Node, 1L, 1)]
    [InlineData(EntityKind.Relationship, 42L, 7)]
    [InlineData(EntityKind.Node, GenerationalRef.SequenceMask, GenerationalRef.MaxGeneration)]
    public void GenerationalRef_roundtrips(EntityKind kind, long seq, int gen)
    {
        long packed = GenerationalRef.Pack(kind, seq, gen);
        GenerationalRef.Kind(packed).Should().Be(kind);
        GenerationalRef.Sequence(packed).Should().Be(seq);
        GenerationalRef.Generation(packed).Should().Be(gen);
    }

    [Fact]
    public void GenerationalRef_rejects_out_of_range()
    {
        Action seqOverflow = () => GenerationalRef.Pack(EntityKind.Node, GenerationalRef.SequenceMask + 1, 0);
        seqOverflow.Should().Throw<ArgumentOutOfRangeException>();

        Action genOverflow = () => GenerationalRef.Pack(EntityKind.Node, 0, GenerationalRef.MaxGeneration + 1);
        genOverflow.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- ABA: slot reuse must not resurrect a stale index entry ----

    [Fact]
    public void Stale_index_entry_is_skipped_after_slot_reuse()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);

        // nodeA を作って "alice" で索引登録。
        NodeId nodeA;
        using (var tx = db.BeginTransaction())
        {
            nodeA = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "alice", nodeA);
            tx.Commit();
        }

        // nodeA を削除 → vacuum で slot を物理回収。
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(nodeA);
            tx.Commit();
        }
        db.Vacuum().ReclaimedNodes.Should().Be(1);

        // 同じ slot を再利用して nodeB を作り "bob" で索引登録。
        NodeId nodeB;
        using (var tx = db.BeginTransaction())
        {
            nodeB = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "bob", nodeB);
            tx.Commit();
        }
        // ABA の前提: 同一 slot が再利用されていること。
        nodeB.Value.Should().Be(nodeA.Value);

        using var rtx = db.BeginReadOnlyTransaction();

        // 旧キー "alice" は世代不一致で stale 検出 → 空 (nodeB を誤って返さない)。
        var stale = rtx.SeekIndex("idx_name", PropertyValue.FromString("alice"));
        stale.MoveNext().Should().BeFalse("再利用された slot の旧索引エントリは世代不一致で弾かれる");
        stale.Dispose();

        // 新キー "bob" は現世代と一致 → nodeB を返す。
        var fresh = rtx.SeekIndex("idx_name", PropertyValue.FromString("bob"));
        fresh.MoveNext().Should().BeTrue();
        fresh.Current.Should().Be(nodeB);
        fresh.MoveNext().Should().BeFalse();
        fresh.Dispose();

        rtx.Rollback();
    }

    [Fact]
    public void RangeIndex_skips_stale_entry_after_slot_reuse()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.Schema.CreateIndex("idx_age", "Person", "age", IndexKind.Int64Equality);

        NodeId nodeA;
        using (var tx = db.BeginTransaction())
        {
            nodeA = tx.CreateNode("Person");
            tx.IndexInsert("idx_age", 30L, nodeA);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(nodeA);
            tx.Commit();
        }
        db.Vacuum().ReclaimedNodes.Should().Be(1);

        NodeId nodeB;
        using (var tx = db.BeginTransaction())
        {
            nodeB = tx.CreateNode("Person");
            tx.IndexInsert("idx_age", 99L, nodeB);
            tx.Commit();
        }
        nodeB.Value.Should().Be(nodeA.Value);

        using var rtx = db.BeginReadOnlyTransaction();

        // 旧範囲 [30,30] は stale → 空。
        var stale = rtx.RangeIndex(
            "idx_age", PropertyValue.FromInt64(30), true, PropertyValue.FromInt64(30), true);
        stale.MoveNext().Should().BeFalse();
        stale.Dispose();

        // 新範囲 [99,99] は nodeB を返す。
        var fresh = rtx.RangeIndex(
            "idx_age", PropertyValue.FromInt64(99), true, PropertyValue.FromInt64(99), true);
        fresh.MoveNext().Should().BeTrue();
        fresh.Current.Should().Be(nodeB);
        fresh.Dispose();

        rtx.Rollback();
    }

    // ---- Orphan GC: generation-mismatch entry is collected & repaired ----

    [Fact]
    public void OrphanGc_collects_and_repairs_generation_mismatch_after_reuse()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);

        NodeId nodeA;
        using (var tx = db.BeginTransaction())
        {
            nodeA = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "alice", nodeA);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(nodeA);
            tx.Commit();
        }
        db.Vacuum().ReclaimedNodes.Should().Be(1);

        NodeId nodeB;
        using (var tx = db.BeginTransaction())
        {
            nodeB = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "bob", nodeB);
            tx.Commit();
        }
        nodeB.Value.Should().Be(nodeA.Value);

        // slot は in-use (nodeB) だが "alice" は世代違いなので orphan。
        var report = db.Diagnostics.CheckIndexConsistency();
        report.EntryCount.Should().Be(2);
        report.OrphanCount.Should().Be(1);
        report.Orphans[0].IndexName.Should().Be("idx_name");
        report.Orphans[0].EntityId.Should().Be(nodeB.Value); // 同一 slot → unpacked seq

        // 修復で stale エントリのみ消える。
        db.Diagnostics.RepairIndexes(IndexRepairMode.Apply).RemovedCount.Should().Be(1);

        var after = db.Diagnostics.CheckIndexConsistency();
        after.OrphanCount.Should().Be(0);
        after.EntryCount.Should().Be(1);

        using var rtx = db.BeginReadOnlyTransaction();
        var fresh = rtx.SeekIndex("idx_name", PropertyValue.FromString("bob"));
        fresh.MoveNext().Should().BeTrue();
        fresh.Current.Should().Be(nodeB);
        fresh.Dispose();
        rtx.Rollback();
    }

    // ---- Format version gate ----

    [Fact]
    public void FormatVersion_current_is_v5()
    {
        // ARCH-4 増分8: 単一ファイル化に伴い V4→V5 へ bump。
        FormatVersion.Current.Should().Be(FormatVersion.V5SingleFile);
    }

    [Fact]
    public void Opening_pre_v4_store_throws_FormatVersionMismatch()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "nodes.db");

        // V4 で 1 ノード書く。
        using (IPagedFile pf = new PagedFile(path))
        {
            var store = new NodeStore(pf);
            store.Allocate(new LabelId(1));
        }

        // ヘッダの format version バイト (page 1, body offset 31 = NodeStore.MetaFormatVersion) を
        // 旧 V3 に書き換える。
        using (IPagedFile pf = new PagedFile(path))
        {
            var ph = pf.PinForWrite(new PageId(1));
            ph.Data[31] = FormatVersion.V3MvccSidecar;
            pf.UnpinDirty(new PageId(1), 0);
        }

        // 再 open は拒否される。
        using (IPagedFile pf = new PagedFile(path))
        {
            Action reopen = () => new NodeStore(pf);
            reopen.Should().Throw<FormatVersionMismatchException>()
                .Which.Found.Should().Be(FormatVersion.V3MvccSidecar);
        }
    }
}

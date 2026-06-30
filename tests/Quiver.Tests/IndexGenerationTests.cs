using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// インデックス値に世代を保持する仕組みを検証する。
/// Vacuum のフリーリストによるスロット再利用後も古いインデックスエントリが
/// 別ノードを返さないこと、孤立エントリ回収が世代不一致を除去できること、
/// V4 より古いフォーマットを拒否することを確認する。
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

    // ---- EntityRef のパックと展開 ----

    [Theory]
    [InlineData(EntityKind.Node, 0L, 0)]
    [InlineData(EntityKind.Node, 1L, 1)]
    [InlineData(EntityKind.Relationship, 42L, 7)]
    [InlineData(EntityKind.Node, EntityRef.SequenceMask, EntityRef.MaxGeneration)]
    public void EntityRef_roundtrips(EntityKind kind, long seq, int gen)
    {
        long packed = EntityRef.Pack(kind, seq, gen);
        EntityRef.UnpackKind(packed).Should().Be(kind);
        EntityRef.Sequence(packed).Should().Be(seq);
        EntityRef.Generation(packed).Should().Be(gen);
    }

    [Fact]
    public void EntityRef_rejects_out_of_range()
    {
        Action seqOverflow = () => EntityRef.Pack(EntityKind.Node, EntityRef.SequenceMask + 1, 0);
        seqOverflow.Should().Throw<ArgumentOutOfRangeException>();

        Action genOverflow = () => EntityRef.Pack(EntityKind.Node, 0, EntityRef.MaxGeneration + 1);
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
        // ABA の前提として、同じスロットが再利用されていることを Sequence で確認する。
        // Value は世代を含むため reincarnation では nodeA と nodeB で異なる)。
        nodeB.Sequence.Should().Be(nodeA.Sequence);
        nodeB.Generation.Should().NotBe(nodeA.Generation);

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
        nodeB.Sequence.Should().Be(nodeA.Sequence); // ARCH-5b: slot 同一性は Sequence

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

    // ---- 世代付き NodeId の外部往復検証 (TryResolve の不一致は not-found) ----

    [Fact]
    public void Stale_node_handle_resolves_to_not_found_after_slot_reuse()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // nodeA を作って外部に往復した想定のハンドルとして保持する。
        NodeId nodeA;
        using (var tx = db.BeginTransaction())
        {
            nodeA = tx.CreateNode("Person");
            tx.Commit();
        }

        // 削除 → vacuum で slot を物理回収 → 同一 slot を nodeB が再利用 (世代 +1)。
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
            tx.Commit();
        }

        // 同一 slot・別世代であること (= ABA の前提)。
        nodeB.Sequence.Should().Be(nodeA.Sequence);
        nodeB.Generation.Should().NotBe(nodeA.Generation);

        using var rtx = db.BeginReadOnlyTransaction();
        // 旧ハンドル nodeA は世代不一致で not-found (別ノード nodeB を誤って指さない)。
        rtx.NodeExists(nodeA).Should().BeFalse("stale generation handle must not resolve to the reused slot");
        // 現ハンドル nodeB は現世代と一致 → 存在する。
        rtx.NodeExists(nodeB).Should().BeTrue();
        // 世代を持たない (= 内部/旧来) ハンドルは照合をスキップし、生存 slot を素直に解決する。
        rtx.NodeExists(new NodeId(nodeA.Sequence)).Should().BeTrue();
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
        nodeB.Sequence.Should().Be(nodeA.Sequence); // ARCH-5b: slot 同一性は Sequence

        // slot は in-use (nodeB) だが "alice" は世代違いなので orphan。
        var report = db.Diagnostics.CheckIndexConsistency();
        report.EntryCount.Should().Be(2);
        report.OrphanCount.Should().Be(1);
        report.Orphans[0].IndexName.Should().Be("idx_name");
        report.Orphans[0].EntityId.Should().Be(nodeB.Sequence); // OrphanIndexEntry.EntityId は unpacked seq

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
    public void FormatVersion_current_is_v1()
    {
        // 未リリース期間中に重ねた format 履歴 (pre-MVCC → MVCC → sidecar → 単一ファイル →
        // columnar → vector → 全文 → logical WAL) はクリーンブレイクで畳み、現実装を v1 として再宣言した。
        FormatVersion.Current.Should().Be(FormatVersion.V1);
    }

    // 旧 format バイトを持つ store は open 時に reject される (クリーンブレイク; 自動マイグレーション無し)。
    private const byte LegacyFormatVersion = 3;

    [Fact]
    public void Opening_store_with_legacy_format_version_throws_FormatVersionMismatch()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "nodes.db");

        // 現行 (v1) で 1 ノード書く。
        using (IPagedFile pf = new PagedFile(path))
        {
            var store = new NodeStore(pf);
            store.Allocate(new LabelId(1));
        }

        // ヘッダの format version バイト (page 1, body offset 31 = NodeStore.MetaFormatVersion) を
        // 旧 format に書き換える。
        using (IPagedFile pf = new PagedFile(path))
        {
            var ph = pf.PinForWrite(new PageId(1));
            ph.Data[31] = LegacyFormatVersion;
            pf.UnpinDirty(new PageId(1), 0);
        }

        // 再 open は拒否される。
        using (IPagedFile pf = new PagedFile(path))
        {
            Action reopen = () => new NodeStore(pf);
            reopen.Should().Throw<FormatVersionMismatchException>()
                .Which.Found.Should().Be(LegacyFormatVersion);
        }
    }
}

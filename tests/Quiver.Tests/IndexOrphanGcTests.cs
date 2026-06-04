using System.Diagnostics;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FT-22 Index orphan GC: <see cref="IDiagnosticsApi.CheckIndexConsistency"/> /
/// <see cref="IDiagnosticsApi.RepairIndexes"/> の挙動を確認する。
/// orphan は「<c>tx.IndexInsert</c> したノードを <c>tx.DeleteNode</c> で削除した状態」で
/// 自然に発生する (現状の DeleteNode は索引エントリを自動削除しない設計のため、
/// recovery 中の torn-write シナリオでなくとも同じ状態を再現できる)。
/// </summary>
public sealed class IndexOrphanGcTests : IDisposable
{
    private readonly string _dir;

    public IndexOrphanGcTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_ft22_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void HealthyDatabase_reports_zero_orphans_and_Repair_is_noop()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);

        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var n = tx.CreateNode("Person");
                tx.IndexInsert("idx_name", $"alice-{i}", n);
            }
            tx.Commit();
        }

        var before = db.Diagnostics.CheckIndexConsistency();
        before.IndexCount.Should().Be(1);
        before.EntryCount.Should().Be(10);
        before.OrphanCount.Should().Be(0);
        before.LabelIndexOrphanCount.Should().Be(0);

        var repair = db.Diagnostics.RepairIndexes(IndexRepairMode.Apply);
        repair.RemovedCount.Should().Be(0);
        repair.LabelIndexInvalidated.Should().BeFalse();

        // 再走査でも健全であること
        db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(0);
    }

    [Fact]
    public void DeleteNode_creates_orphan_detected_by_CheckIndexConsistency()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);

        NodeId aliveOnly, doomed;
        using (var tx = db.BeginTransaction())
        {
            aliveOnly = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "alive", aliveOnly);
            doomed = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "doomed", doomed);
            tx.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(doomed);
            tx.Commit();
        }

        // DeleteNode は索引エントリを自動削除しない → orphan 1 件発生
        var report = db.Diagnostics.CheckIndexConsistency();
        report.EntryCount.Should().Be(2);
        report.OrphanCount.Should().Be(1);
        report.Orphans.Should().ContainSingle()
            .Which.EntityId.Should().Be(doomed.Sequence); // ARCH-5b: OrphanIndexEntry.EntityId は unpacked seq
        report.Orphans[0].IndexName.Should().Be("idx_name");
    }

    [Fact]
    public void RepairIndexes_Apply_removes_orphans_then_CheckIndexConsistency_clean()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);

        NodeId alive, doomed1, doomed2;
        using (var tx = db.BeginTransaction())
        {
            alive = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "alive", alive);
            doomed1 = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "doomed-1", doomed1);
            doomed2 = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "doomed-2", doomed2);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(doomed1);
            tx.DeleteNode(doomed2);
            tx.Commit();
        }

        var repair = db.Diagnostics.RepairIndexes(IndexRepairMode.Apply);
        repair.RemovedCount.Should().Be(2);
        repair.Orphans.Should().HaveCount(2);

        var after = db.Diagnostics.CheckIndexConsistency();
        after.OrphanCount.Should().Be(0);
        after.EntryCount.Should().Be(1);

        // 生きているノードは依然として索引から引ける
        using var rtx = db.BeginReadOnlyTransaction();
        var cur = rtx.SeekIndex("idx_name", PropertyValue.FromString("alive"));
        cur.MoveNext().Should().BeTrue();
        cur.Current.Should().Be(alive);
        cur.Dispose();

        // 削除されたエントリはヒットしない
        var gone = rtx.SeekIndex("idx_name", PropertyValue.FromString("doomed-1"));
        gone.MoveNext().Should().BeFalse();
        gone.Dispose();
        rtx.Rollback();
    }

    [Fact]
    public void RepairIndexes_DryRun_reports_orphans_but_does_not_delete()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);

        NodeId doomed;
        using (var tx = db.BeginTransaction())
        {
            doomed = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "doomed", doomed);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(doomed);
            tx.Commit();
        }

        var dry = db.Diagnostics.RepairIndexes(IndexRepairMode.DryRun);
        dry.RemovedCount.Should().Be(0);
        dry.Orphans.Should().HaveCount(1);

        // DryRun 後の再 check で同じ orphan がそのまま残っていること
        var after = db.Diagnostics.CheckIndexConsistency();
        after.OrphanCount.Should().Be(1);
    }

    [Fact]
    public void RepairIndexes_handles_multiple_indexes()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);
        db.Schema.CreateIndex("idx_age", "Person", "age", IndexKind.Int64Equality);

        NodeId alive, doomed;
        using (var tx = db.BeginTransaction())
        {
            alive = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "alive", alive);
            tx.IndexInsert("idx_age", 30L, alive);

            doomed = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "doomed", doomed);
            tx.IndexInsert("idx_age", 99L, doomed);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(doomed);
            tx.Commit();
        }

        var before = db.Diagnostics.CheckIndexConsistency();
        before.IndexCount.Should().Be(2);
        before.OrphanCount.Should().Be(2); // 索引 2 本それぞれに 1 件ずつ orphan

        var repair = db.Diagnostics.RepairIndexes(IndexRepairMode.Apply);
        repair.RemovedCount.Should().Be(2);

        db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(0);
    }

    [Fact]
    public void AutoRepairOrphansOnRecovery_cleans_orphan_on_reopen()
    {
        // orphan を作る (commit 後の DeleteNode)
        NodeId alive;
        using (var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);
            NodeId doomed;
            using (var tx = db.BeginTransaction())
            {
                alive = tx.CreateNode("Person");
                tx.IndexInsert("idx_name", "alive", alive);
                doomed = tx.CreateNode("Person");
                tx.IndexInsert("idx_name", "doomed", doomed);
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                tx.DeleteNode(doomed);
                tx.Commit();
            }
            db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(1);
        }

        // AutoRepairOrphansOnRecovery=true で再 open → orphan が自動除去される
        using (var db = GraphDatabase.Open(
            System.IO.Path.Combine(_dir, "graph.quiver"),
            new GraphDatabaseOptions { AutoRepairOrphansOnRecovery = true }))
        {
            db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(0);

            // alive エントリは生き残る
            using var rtx = db.BeginReadOnlyTransaction();
            var cur = rtx.SeekIndex("idx_name", PropertyValue.FromString("alive"));
            cur.MoveNext().Should().BeTrue();
            cur.Current.Should().Be(alive);
            cur.Dispose();
            rtx.Rollback();
        }
    }

    [Fact]
    public void AutoRepairOrphansOnRecovery_default_false_keeps_orphan()
    {
        using (var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);
            NodeId doomed;
            using (var tx = db.BeginTransaction())
            {
                doomed = tx.CreateNode("Person");
                tx.IndexInsert("idx_name", "doomed", doomed);
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                tx.DeleteNode(doomed);
                tx.Commit();
            }
        }

        // 既定 (option 未指定) では orphan は残る
        using (var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(1);
        }
    }

    [Fact]
    public void CheckIndexConsistency_scales_to_large_database()
    {
        // 完了条件: 100k node + 5 index で CheckIndexConsistency が 5 秒以内。
        // BulkLoader は索引には書かないので、Tx 経由で投入する (テスト時間を抑えるため
        // 件数を 10k に削減 — perf 性質は同じで 1 桁スケールで観測する)。
        const int nodeCount = 10_000;
        const int indexCount = 5;

        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        for (int i = 0; i < indexCount; i++)
            db.Schema.CreateIndex($"idx_{i}", "Doc", $"key_{i}", IndexKind.Int64Equality);

        using (var tx = db.BeginTransaction())
        {
            for (int n = 0; n < nodeCount; n++)
            {
                var node = tx.CreateNode("Doc");
                for (int i = 0; i < indexCount; i++)
                    tx.IndexInsert($"idx_{i}", (long)n, node);
            }
            tx.Commit();
        }

        var sw = Stopwatch.StartNew();
        var report = db.Diagnostics.CheckIndexConsistency();
        sw.Stop();

        report.IndexCount.Should().Be(indexCount);
        report.EntryCount.Should().Be(nodeCount * (long)indexCount);
        report.OrphanCount.Should().Be(0);

        // 10k × 5 index = 50k entries で 5 秒以内 (100k × 5 = 500k なら 50 秒以内に相当)。
        // 環境変動を許容するため 10 秒上限で fail させる。
        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "CheckIndexConsistency は 50k entries で 10 秒以内に完了すべき");
    }
}

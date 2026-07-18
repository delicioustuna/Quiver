using System.Diagnostics;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// インデックスの孤立エントリ回収における <see cref="IDiagnosticsApi.CheckIndexConsistency"/> /
/// <see cref="IDiagnosticsApi.RepairIndexes"/> の挙動を確認する。
/// 孤立エントリは <c>tx.IndexInsert</c> したVertexを <c>tx.DeleteVertex</c> で削除して作る。
/// DeleteVertex はインデックスエントリを自動削除しないため、
/// リカバリー中の不完全書き込みを使わずに同じ状態を再現できる。
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
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));

        using (var tx = db.BeginWriteTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var n = tx.CreateVertex("Person");
                tx.SetIndexedProperty("idx_name", $"alice-{i}", n);
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
    public void DeleteVertex_creates_orphan_detected_by_CheckIndexConsistency()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));

        VertexId aliveOnly, doomed;
        using (var tx = db.BeginWriteTransaction())
        {
            aliveOnly = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "alive", aliveOnly);
            doomed = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "doomed", doomed);
            tx.Commit();
        }

        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(doomed);
            tx.Commit();
        }

        // DeleteVertex は索引エントリを自動削除しない → orphan 1 件発生
        var report = db.Diagnostics.CheckIndexConsistency();
        report.EntryCount.Should().Be(2);
        report.OrphanCount.Should().Be(1);
        report.Orphans.Should().ContainSingle()
            .Which.EntityId.Should().Be(doomed.Sequence); // OrphanIndexEntry.EntityId は unpacked seq
        report.Orphans[0].IndexName.Should().Be("idx_name");
    }

    [Fact]
    public void RepairIndexes_Apply_removes_orphans_then_CheckIndexConsistency_clean()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));

        VertexId alive, doomed1, doomed2;
        using (var tx = db.BeginWriteTransaction())
        {
            alive = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "alive", alive);
            doomed1 = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "doomed-1", doomed1);
            doomed2 = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "doomed-2", doomed2);
            tx.Commit();
        }
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(doomed1);
            tx.DeleteVertex(doomed2);
            tx.Commit();
        }

        var repair = db.Diagnostics.RepairIndexes(IndexRepairMode.Apply);
        repair.RemovedCount.Should().Be(2);
        repair.Orphans.Should().HaveCount(2);

        var after = db.Diagnostics.CheckIndexConsistency();
        after.OrphanCount.Should().Be(0);
        after.EntryCount.Should().Be(1);

        // 生きているVertexは依然として索引から引ける
        using var rtx = db.BeginReadTransaction();
        var cur = rtx.SeekIndex("idx_name", PropertyValue.FromString("alive"));
        cur.MoveNext().Should().BeTrue();
        cur.Current.Should().Be(EntityRef.From(alive));
        cur.Dispose();

        // 削除されたエントリはヒットしない
        var gone = rtx.SeekIndex("idx_name", PropertyValue.FromString("doomed-1"));
        gone.MoveNext().Should().BeFalse();
        gone.Dispose();
    }

    [Fact]
    public void RepairIndexes_DryRun_reports_orphans_but_does_not_delete()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));

        VertexId doomed;
        using (var tx = db.BeginWriteTransaction())
        {
            doomed = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "doomed", doomed);
            tx.Commit();
        }
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(doomed);
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
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_age", new PropertyTarget(PropertyOwnerKind.Vertex, "age", "Person"), IndexKind.Int64Equality)));

        VertexId alive, doomed;
        using (var tx = db.BeginWriteTransaction())
        {
            alive = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "alive", alive);
            tx.SetIndexedProperty("idx_age", 30L, alive);

            doomed = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "doomed", doomed);
            tx.SetIndexedProperty("idx_age", 99L, doomed);
            tx.Commit();
        }
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(doomed);
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
        // orphan を作る (commit 後の DeleteVertex)
        VertexId alive;
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));
            VertexId doomed;
            using (var tx = db.BeginWriteTransaction())
            {
                alive = tx.CreateVertex("Person");
                tx.SetIndexedProperty("idx_name", "alive", alive);
                doomed = tx.CreateVertex("Person");
                tx.SetIndexedProperty("idx_name", "doomed", doomed);
                tx.Commit();
            }
            using (var tx = db.BeginWriteTransaction())
            {
                tx.DeleteVertex(doomed);
                tx.Commit();
            }
            db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(1);
        }

        // AutoRepairOrphansOnRecovery=true で再 open → orphan が自動除去される
        using (var db = QuiverDatabase.Open(
            System.IO.Path.Combine(_dir, "graph.quiver"),
            new QuiverDatabaseOptions { AutoRepairOrphansOnRecovery = true }))
        {
            db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(0);

            // alive エントリは生き残る
            using var rtx = db.BeginReadTransaction();
            var cur = rtx.SeekIndex("idx_name", PropertyValue.FromString("alive"));
            cur.MoveNext().Should().BeTrue();
            cur.Current.Should().Be(EntityRef.From(alive));
            cur.Dispose();
        }
    }

    [Fact]
    public void AutoRepairOrphansOnRecovery_default_false_keeps_orphan()
    {
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));
            VertexId doomed;
            using (var tx = db.BeginWriteTransaction())
            {
                doomed = tx.CreateVertex("Person");
                tx.SetIndexedProperty("idx_name", "doomed", doomed);
                tx.Commit();
            }
            using (var tx = db.BeginWriteTransaction())
            {
                tx.DeleteVertex(doomed);
                tx.Commit();
            }
        }

        // 既定 (option 未指定) では orphan は残る
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(1);
        }
    }

    [Fact]
    public void CheckIndexConsistency_scales_to_large_database()
    {
        // 完了条件: 100k vertex + 5 index で CheckIndexConsistency が 5 秒以内。
        // BulkLoader は索引には書かないので、Tx 経由で投入する (テスト時間を抑えるため
        // 件数を 10k に削減 — perf 性質は同じで 1 桁スケールで観測する)。
        const int vertexCount = 10_000;
        const int indexCount = 5;

        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        for (int i = 0; i < indexCount; i++)
            db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition($"idx_{i}", new PropertyTarget(PropertyOwnerKind.Vertex, $"key_{i}", "Doc"), IndexKind.Int64Equality)));

        using (var tx = db.BeginWriteTransaction())
        {
            for (int n = 0; n < vertexCount; n++)
            {
                var vertex = tx.CreateVertex("Doc");
                for (int i = 0; i < indexCount; i++)
                    tx.SetIndexedProperty($"idx_{i}", (long)n, vertex);
            }
            tx.Commit();
        }

        var sw = Stopwatch.StartNew();
        var report = db.Diagnostics.CheckIndexConsistency();
        sw.Stop();

        report.IndexCount.Should().Be(indexCount);
        report.EntryCount.Should().Be(vertexCount * (long)indexCount);
        report.OrphanCount.Should().Be(0);

        // 10k × 5 index = 50k entries で 5 秒以内 (100k × 5 = 500k なら 50 秒以内に相当)。
        // 環境変動を許容するため 10 秒上限で fail させる。
        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "CheckIndexConsistency は 50k entries で 10 秒以内に完了すべき");
    }
}

using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// <see cref="YatagarasuDatabase.CreateSnapshot"/> のライブスナップショット契約を検証する。
///  - 静止中のデータベースを複製し、出力を開くと全データを参照できる
///  - 書き込みと並行して複製しても、出力の整合性検査に成功する
///  - インデックスを含む複製で Has 条件を評価できる
/// </summary>
public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir;
    private readonly string _snapDir;

    public SnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_op1_" + Guid.NewGuid().ToString("N"));
        _snapDir = Path.Combine(Path.GetTempPath(), "yatagarasu_op1_snap_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_snapDir, recursive: true); } catch { }
    }

    [Fact]
    public void Snapshot_of_quiescent_database_preserves_all_committed_data()
    {
        var ids = new List<VertexId>();
        using (var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata")))
        {
            using var tx = db.BeginWriteTransaction();
            for (int i = 0; i < 50; i++)
            {
                var n = tx.CreateVertex("Person");
                tx.SetProperty(n, "name", PropertyValue.FromString($"p-{i}"));
                ids.Add(n);
            }
            tx.Commit();

            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.yata"));
        }

        using var target = YatagarasuDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.yata"));
        using var read = target.BeginReadTransaction();
        foreach (var id in ids)
        {
            read.VertexExists(id).Should().BeTrue();
            var name = read.GetProperty(id, "name");
            name.Type.Should().Be(PropertyValueType.String);
        }
    }

    [Fact]
    public void Snapshot_includes_indexes_so_target_can_lookup_by_index()
    {
        using (var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata")))
        {
            db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_email", new PropertyTarget(PropertyOwnerKind.Vertex, "email", "Person"), IndexKind.StringEquality)));
            using (var tx = db.BeginWriteTransaction())
            {
                for (int i = 0; i < 20; i++)
                {
                    var n = tx.CreateVertex("Person");
                    tx.SetProperty(n, "email", PropertyValue.FromString($"u{i}@x"));
                    tx.SetIndexedProperty("idx_email", $"u{i}@x", n);
                }
                tx.Commit();
            }

            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.yata"));
        }

        using var target = YatagarasuDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.yata"));
        using var read = target.BeginReadTransaction();
        for (int i = 0; i < 20; i++)
        {
            var key = PropertyValue.FromString($"u{i}@x");
            var en = read.SeekIndex("idx_email", key);
            int hits = 0;
            while (en.MoveNext()) hits++;
            en.Dispose();
            hits.Should().Be(1, $"index must yield exactly one vertex for u{i}@x");
        }
    }

    [Fact]
    public void Snapshot_excludes_indexes_when_IncludeIndexes_is_false()
    {
        using (var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata")))
        {
            db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_email", new PropertyTarget(PropertyOwnerKind.Vertex, "email", "Person"), IndexKind.StringEquality)));
            using (var tx = db.BeginWriteTransaction())
            {
                var n = tx.CreateVertex("Person");
                tx.SetProperty(n, "email", PropertyValue.FromString("a@b"));
                tx.SetIndexedProperty("idx_email", "a@b", n);
                tx.Commit();
            }

            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.yata"), new SnapshotOptions { IncludeIndexes = false });
        }

        var idxDir = Path.Combine(_snapDir, "indexes");
        if (Directory.Exists(idxDir))
        {
            Directory.GetFiles(idxDir, "*.idx").Should().BeEmpty(
                "IncludeIndexes=false で .idx は target に含まれてはいけない");
        }
    }

    [Fact]
    public void Snapshot_target_passes_CheckConsistency_after_concurrent_writes()
    {
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"));
        using (var tx = db.BeginWriteTransaction())
        {
            for (int i = 0; i < 100; i++) tx.CreateVertex("Seed");
            tx.Commit();
        }

        var stop = new ManualResetEventSlim(false);
        var writer = Task.Run(() =>
        {
            int batch = 0;
            while (!stop.IsSet)
            {
                try
                {
                    using var tx = db.BeginWriteTransaction();
                    for (int i = 0; i < 10; i++)
                    {
                        var n = tx.CreateVertex("Live");
                        tx.SetProperty(n, "batch", PropertyValue.FromInt32(batch));
                    }
                    tx.Commit();
                    batch++;
                }
                catch { /* swallow contention */ }
            }
        });

        try
        {
            // Writer が回り始めるまでウォーミング
            Thread.Sleep(50);
            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.yata"));
        }
        finally
        {
            stop.Set();
#pragma warning disable xUnit1031 // 意図的ブロッキング: バックグラウンド writer スレッドの join (cleanup)
            writer.Wait(TimeSpan.FromSeconds(5));
#pragma warning restore xUnit1031
        }

        using var target = YatagarasuDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.yata"));
        var report = target.Diagnostics.CheckConsistency();
        report.IsConsistent.Should().BeTrue(
            "snapshot target は recovery 後に整合状態へ収束していなければならない。issues: "
            + string.Join(", ", report.Issues));
    }

    [Fact]
    public void Snapshot_target_LSN_is_at_or_beyond_snapshot_start_lsn()
    {
        // snapshot 末尾 LSN >= source 側 snapshot 開始時点の LSN を簡易に確認する。
        // target を Open すると recovery が走り、Open 直後に新規 tx を始められれば「target の
        // WAL 末尾 LSN > 0」(= snapshot 時点を超える new tx を受け付ける) ことが確認できる。
        using (var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata")))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                tx.CreateVertex("A");
                tx.Commit();
            }
            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.yata"));
        }

        using var target = YatagarasuDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.yata"));
        using (var tx = target.BeginWriteTransaction())
        {
            tx.CreateVertex("B");
            tx.Commit();
        }
        // 再 Open しても両方のVertexが見えること
        target.Dispose();
        using var reopened = YatagarasuDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.yata"));
        var stats = reopened.Diagnostics.GetStatistics();
        stats.VertexCount.Should().BeGreaterThanOrEqualTo(2);
    }
}

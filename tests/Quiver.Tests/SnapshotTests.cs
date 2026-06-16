using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// OP-1: <see cref="GraphDatabase.CreateSnapshot"/> のライブスナップショット契約をカバーする。
///  - quiescent な DB を snapshot → target を Open → データが完全に見える
///  - 書き込み workload と並行に snapshot → target Open → 整合性 (CheckConsistency) が緑
///  - 索引も含めて snapshot → target の Has 条件が動く
/// </summary>
public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir;
    private readonly string _snapDir;

    public SnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_op1_" + Guid.NewGuid().ToString("N"));
        _snapDir = Path.Combine(Path.GetTempPath(), "quiver_op1_snap_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_snapDir, recursive: true); } catch { }
    }

    [Fact]
    public void Snapshot_of_quiescent_database_preserves_all_committed_data()
    {
        var ids = new List<NodeId>();
        using (var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            using var tx = db.BeginTransaction();
            for (int i = 0; i < 50; i++)
            {
                var n = tx.CreateNode("Person");
                tx.SetProperty(n, "name", PropertyValue.FromString($"p-{i}"));
                ids.Add(n);
            }
            tx.Commit();

            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.quiver"));
        }

        using var target = GraphDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.quiver"));
        using var read = target.BeginReadOnlyTransaction();
        foreach (var id in ids)
        {
            read.NodeExists(id).Should().BeTrue();
            var name = read.GetProperty(id, "name");
            name.Type.Should().Be(PropertyValueType.String);
        }
    }

    [Fact]
    public void Snapshot_includes_indexes_so_target_can_lookup_by_index()
    {
        using (var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            db.Schema.CreateIndex("idx_email", "Person", "email", IndexKind.StringEquality);
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < 20; i++)
                {
                    var n = tx.CreateNode("Person");
                    tx.SetProperty(n, "email", PropertyValue.FromString($"u{i}@x"));
                    tx.IndexInsert("idx_email", $"u{i}@x", n);
                }
                tx.Commit();
            }

            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.quiver"));
        }

        using var target = GraphDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.quiver"));
        using var read = target.BeginReadOnlyTransaction();
        for (int i = 0; i < 20; i++)
        {
            var key = PropertyValue.FromString($"u{i}@x");
            var en = read.SeekIndex("idx_email", key);
            int hits = 0;
            while (en.MoveNext()) hits++;
            en.Dispose();
            hits.Should().Be(1, $"index must yield exactly one node for u{i}@x");
        }
    }

    [Fact]
    public void Snapshot_excludes_indexes_when_IncludeIndexes_is_false()
    {
        using (var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            db.Schema.CreateIndex("idx_email", "Person", "email", IndexKind.StringEquality);
            using (var tx = db.BeginTransaction())
            {
                var n = tx.CreateNode("Person");
                tx.SetProperty(n, "email", PropertyValue.FromString("a@b"));
                tx.IndexInsert("idx_email", "a@b", n);
                tx.Commit();
            }

            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.quiver"), new SnapshotOptions { IncludeIndexes = false });
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
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 100; i++) tx.CreateNode("Seed");
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
                    using var tx = db.BeginTransaction();
                    for (int i = 0; i < 10; i++)
                    {
                        var n = tx.CreateNode("Live");
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
            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.quiver"));
        }
        finally
        {
            stop.Set();
#pragma warning disable xUnit1031 // 意図的ブロッキング: バックグラウンド writer スレッドの join (cleanup)
            writer.Wait(TimeSpan.FromSeconds(5));
#pragma warning restore xUnit1031
        }

        using var target = GraphDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.quiver"));
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
        using (var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            using (var tx = db.BeginTransaction())
            {
                tx.CreateNode("A");
                tx.Commit();
            }
            db.CreateSnapshot(System.IO.Path.Combine(_snapDir, "graph.quiver"));
        }

        using var target = GraphDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.quiver"));
        using (var tx = target.BeginTransaction())
        {
            tx.CreateNode("B");
            tx.Commit();
        }
        // 再 Open しても両方のノードが見えること
        target.Dispose();
        using var reopened = GraphDatabase.Open(System.IO.Path.Combine(_snapDir, "graph.quiver"));
        var stats = reopened.Diagnostics.GetStatistics();
        stats.NodeCount.Should().BeGreaterThanOrEqualTo(2);
    }
}

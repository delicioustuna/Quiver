using FluentAssertions;
using Quiver.Maintenance;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 自動 Vacuum ワーカーの周期起動、逐次実行ガード、例外処理、破棄時の停止と、
/// <see cref="QuiverDatabase"/> のオープンおよび破棄との連携を確認する。
/// </summary>
public sealed class AutoVacuumWorkerTests : IDisposable
{
    private readonly string _dir;

    public AutoVacuumWorkerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_op7_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static VacuumReport DummyReport(bool skipped = false) =>
        new(ReclaimedVertices: 0, ReclaimedEdges: 0, ReclaimedProperties: 0,
            PrunedCommittedTxEntries: 0, ElapsedMs: 0, HorizonTxId: 1, Skipped: skipped);

    [Fact]
    public void RunOnce_invokes_vacuum_and_counts_runs()
    {
        int calls = 0;
        using var worker = new AutoVacuumWorker(
            () => { calls++; return DummyReport(); }, TimeSpan.FromHours(1));

        worker.RunOnce().Should().NotBeNull();
        worker.RunOnce().Should().NotBeNull();

        calls.Should().Be(2);
        worker.RunCount.Should().Be(2);
        worker.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void RunOnce_tracks_skipped_reports()
    {
        using var worker = new AutoVacuumWorker(
            () => DummyReport(skipped: true), TimeSpan.FromHours(1));

        worker.RunOnce();
        worker.RunOnce();

        worker.RunCount.Should().Be(2);
        worker.SkippedCount.Should().Be(2);
    }

    [Fact]
    public void Vacuum_exception_is_swallowed_and_does_not_crash_worker()
    {
        using var worker = new AutoVacuumWorker(
            () => throw new InvalidOperationException("boom"), TimeSpan.FromHours(1));

        // 例外は握り潰され null が返る。RunCount は増えない (vacuum が完了していないため)。
        var act = () => worker.RunOnce();
        act.Should().NotThrow();
        worker.RunOnce().Should().BeNull();
        worker.RunCount.Should().Be(0);
    }

    [Fact]
    public void Reentrant_tick_is_skipped_while_previous_runs()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        AutoVacuumWorker? worker = null;
        worker = new AutoVacuumWorker(
            () =>
            {
                entered.Set();
                gate.Wait(); // 最初の tick をブロックし続ける
                return DummyReport();
            }, TimeSpan.FromHours(1));

        // 別スレッドで 1 個目の tick を起動して中で待たせる。
        var t = new Thread(() => worker!.RunOnce()) { IsBackground = true };
        t.Start();
        entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        // 1 個目が走っている間の 2 個目はガードで skip され null。
        worker!.RunOnce().Should().BeNull();

        gate.Set();      // 1 個目を解放
        t.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
        worker.RunCount.Should().Be(1);
        worker.Dispose();
    }

    [Fact]
    public void Timer_fires_periodically()
    {
        int calls = 0;
        using var fired = new CountdownEvent(2);
        using var worker = new AutoVacuumWorker(
            () => { Interlocked.Increment(ref calls); return DummyReport(); },
            TimeSpan.FromMilliseconds(50));
        worker.OnTickCompleted = _ => { try { fired.Signal(); } catch (InvalidOperationException) { } };

        fired.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("timer should fire at least twice");
        calls.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Dispose_is_idempotent_and_stops_ticks()
    {
        var worker = new AutoVacuumWorker(() => DummyReport(), TimeSpan.FromMilliseconds(20));
        Thread.Sleep(60);
        worker.Dispose();
        long countAfterDispose = worker.RunCount;
        Thread.Sleep(80);
        // Dispose 後は tick が増えない。
        worker.RunCount.Should().Be(countAfterDispose);
        // 二重 Dispose は no-op。
        var act = () => worker.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_rejects_nonpositive_interval()
    {
        var act = () => new AutoVacuumWorker(() => DummyReport(), TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- QuiverDatabase 配線 ----

    [Fact]
    public void QuiverDatabase_disabled_AutoVacuum_does_not_auto_run()
    {
        // 既定 (AutoVacuum=false) では open しても自動 vacuum は走らない。
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = db.BeginTransaction())
        {
            tx.CreateVertex("Person");
            tx.Commit();
        }
        // worker 不在の証明として、明示 Vacuum だけが効くことを確認 (例外なく open/dispose できる)。
        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
    }

    [Fact]
    public void QuiverDatabase_enabled_AutoVacuum_reclaims_dead_versions_in_background()
    {
        var options = new QuiverDatabaseOptions
        {
            AutoVacuum = true,
            AutoVacuumInterval = TimeSpan.FromMilliseconds(50),
        };
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), options);

        // Vertexを作って全削除 → バックグラウンド worker が回収するのを待つ。
        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 50; i++) ids.Add(tx.CreateVertex("Person").Sequence); // slot は Sequence
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            foreach (var id in ids) tx.DeleteVertex(new Core.VertexId(id));
            tx.Commit();
        }

        // 物理回収が起きると、次に作るVertexは free list の低い ID を再利用する。
        // worker tick (50ms 周期) が走ってスロットが戻るのを最大 5 秒待つ。
        long reusedId = -1;
        bool reused = SpinWaitUntil(() =>
        {
            using var tx = db.BeginTransaction();
            long newId = tx.CreateVertex("Person").Sequence; // 再利用判定は Sequence
            tx.Commit();
            reusedId = newId;
            // 回収済みなら元の範囲 [0..49] のどれかを再利用するはず。
            return newId <= 49;
        }, TimeSpan.FromSeconds(5));

        reused.Should().BeTrue(
            $"background AutoVacuum should reclaim vertex slots, but new id was {reusedId}");
    }

    [Fact]
    public void QuiverDatabase_Dispose_stops_worker_cleanly()
    {
        var options = new QuiverDatabaseOptions
        {
            AutoVacuum = true,
            AutoVacuumInterval = TimeSpan.FromMilliseconds(30),
        };
        var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), options);
        using (var tx = db.BeginTransaction())
        {
            tx.CreateVertex("Person");
            tx.Commit();
        }
        Thread.Sleep(80); // 何 tick か回す
        // Dispose は worker 停止 → backend close の順で、例外なく完了する。
        var act = () => db.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void AutoVacuum_with_zero_interval_does_not_start_worker()
    {
        var options = new QuiverDatabaseOptions
        {
            AutoVacuum = true,
            AutoVacuumInterval = TimeSpan.Zero, // 無効化
        };
        // ワーカーは立たないが open/dispose は問題なく通る。
        var act = () =>
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), options);
            using var tx = db.BeginTransaction();
            tx.CreateVertex("Person");
            tx.Commit();
        };
        act.Should().NotThrow();
    }

    private static bool SpinWaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }
}

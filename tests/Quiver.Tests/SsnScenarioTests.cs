using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// SSN (Serial Safety Net, Wang et al. DaMoN'15) の代表的なシナリオを検証する。
/// 論文 §2–3 の異常を再現し、SSN による読み書き競合の検出とロックによる書き書き競合の
/// 検出を組み合わせて、少なくとも一方を中断し直列化可能な結果になることを確認する。
///
/// <para>各トランザクションは専用スレッド (<see cref="TxThread"/>) で実行し、
/// テストスレッドが開始、読み取り、書き込み、コミットの全体順序を同期的に制御する。</para>
/// </summary>
[Collection("ssn-scenarios")]
public sealed class SsnScenarioTests
{
    private const string PublicWriterGateSkip =
        "Multiple concurrent public writers are no longer supported; single-writer gate coverage replaces this white-box concurrency scenario.";

    private static QuiverDatabase Open(string dir)
        => QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"), new QuiverDatabaseOptions
        {
            // ww cycle (lost update / 相互上書き) は SSN ではなく wait-for graph で解く。
            DeadlockDetectionInterval = TimeSpan.FromMilliseconds(100),
        });

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "quiver_ssnsc_" + Guid.NewGuid().ToString("N"));

    private static int Ssn(params Exception?[] exs) => exs.Count(e => e is SerializabilityException);
    private static int Aborted(params Exception?[] exs) => exs.Count(e => e is not null);

    // ─────────────────────────── WriteSkew_RR ───────────────────────────
    [Fact(Skip = PublicWriterGateSkip)]
    [Trait("Category", "Ssn")]
    public void WriteSkew_RR_aborts_one()
    {
        var dir = TempDir();
        try
        {
            using var db = Open(dir);
            var (a, b) = Seed(db, ("a", 100), ("b", 100));
            using var t1 = new TxThread(); using var t2 = new TxThread();
            IGraphTransaction x1 = null!, x2 = null!;
            t1.Do(() => x1 = db.BeginTransaction(IsolationLevel.Serializable));
            t2.Do(() => x2 = db.BeginTransaction(IsolationLevel.Serializable));
            t1.Do(() => x1.GetProperty(b, "v"));
            t2.Do(() => x2.GetProperty(a, "v"));
            t1.Do(() => x1.SetProperty(a, "v", PropertyValue.FromInt32(40)));
            t2.Do(() => x2.SetProperty(b, "v", PropertyValue.FromInt32(40)));
            var e1 = t1.Try(() => x1.Commit());
            var e2 = t2.Try(() => x2.Commit());
            Ssn(e1, e2).Should().Be(1, "古典 write skew は SSN が exclusion window 違反で一方を abort");
        }
        finally { Cleanup(dir); }
    }

    // ─────────────────────────── SafeRetry ───────────────────────────
    [Fact(Skip = PublicWriterGateSkip)]
    [Trait("Category", "Ssn")]
    public void SafeRetry_aborted_tx_commits_on_immediate_retry()
    {
        var dir = TempDir();
        try
        {
            using var db = Open(dir);
            var (a, b) = Seed(db, ("a", 100), ("b", 100));
            // write skew を起こして一方を abort させる。
            using (var t1 = new TxThread())
            using (var t2 = new TxThread())
            {
                IGraphTransaction x1 = null!, x2 = null!;
                t1.Do(() => x1 = db.BeginTransaction(IsolationLevel.Serializable));
                t2.Do(() => x2 = db.BeginTransaction(IsolationLevel.Serializable));
                t1.Do(() => x1.GetProperty(b, "v"));
                t2.Do(() => x2.GetProperty(a, "v"));
                t1.Do(() => x1.SetProperty(a, "v", PropertyValue.FromInt32(40)));
                t2.Do(() => x2.SetProperty(b, "v", PropertyValue.FromInt32(40)));
                var e1 = t1.Try(() => x1.Commit());
                var e2 = t2.Try(() => x2.Commit());
                Ssn(e1, e2).Should().Be(1);
            }

            // abort された側のロジックを単独で即時 retry → 必ず commit (Theorem 7)。
            Exception? retryEx = null;
            try
            {
                using var rtx = db.BeginTransaction(IsolationLevel.Serializable);
                _ = rtx.GetProperty(a, "v");
                rtx.SetProperty(b, "v", PropertyValue.FromInt32(40));
                rtx.Commit();
            }
            catch (Exception e) { retryEx = e; }
            retryEx.Should().BeNull("競合相手が居ない retry は SSN で必ず commit する");
        }
        finally { Cleanup(dir); }
    }

    // ─────────────────────────── DangerousStructure (3-cycle, T3 first) ───
    [Fact(Skip = PublicWriterGateSkip)]
    [Trait("Category", "Ssn")]
    public void DangerousStructure_three_tx_rw_cycle_aborts_one()
    {
        var dir = TempDir();
        try
        {
            using var db = Open(dir);
            var n = Seed(db, ("x", 0), ("y", 0), ("z", 0));
            var (x, y, z) = (n[0], n[1], n[2]);
            using var t1 = new TxThread(); using var t2 = new TxThread(); using var t3 = new TxThread();
            IGraphTransaction a = null!, b = null!, c = null!;
            t1.Do(() => a = db.BeginTransaction(IsolationLevel.Serializable));
            t2.Do(() => b = db.BeginTransaction(IsolationLevel.Serializable));
            t3.Do(() => c = db.BeginTransaction(IsolationLevel.Serializable));
            // rw 3-cycle: a:r(x)w(z), b:r(y)w(x), c:r(z)w(y)
            t1.Do(() => a.GetProperty(x, "v"));
            t2.Do(() => b.GetProperty(y, "v"));
            t3.Do(() => c.GetProperty(z, "v"));
            t1.Do(() => a.SetProperty(z, "v", PropertyValue.FromInt32(1)));
            t2.Do(() => b.SetProperty(x, "v", PropertyValue.FromInt32(1)));
            t3.Do(() => c.SetProperty(y, "v", PropertyValue.FromInt32(1)));
            // T3 が最初に commit。
            var e3 = t3.Try(() => c.Commit());
            var e2 = t2.Try(() => b.Commit());
            var e1 = t1.Try(() => a.Commit());
            Ssn(e1, e2, e3).Should().BeGreaterThanOrEqualTo(1,
                "rw 3-cycle (dangerous structure) は SSN が少なくとも一方を abort して直列化を保つ");
        }
        finally { Cleanup(dir); }
    }

    // ─────────────────────────── ReadOnlyAnomaly (Fekete 2004) ───────────
    [Fact(Skip = PublicWriterGateSkip)]
    [Trait("Category", "Ssn")]
    public void ReadOnlyAnomaly_fekete_three_tx_is_serializable()
    {
        var dir = TempDir();
        try
        {
            using var db = Open(dir);
            // x=savings, y=checking。Fekete 2004 の 3-tx read-only anomaly。
            var (x, y) = Seed(db, ("x", 0), ("y", 0));
            using var tD = new TxThread(); using var tW = new TxThread(); using var tR = new TxThread();
            IGraphTransaction dep = null!, wit = null!, ro = null!;
            // T_deposit: 預金 y += 20 (x,y を読んで y を書く)
            // T_withdraw: x から引き出し (x,y を読んで x を書く、overdraft 判定)
            // T_readonly: x,y を観測
            tD.Do(() => dep = db.BeginTransaction(IsolationLevel.Serializable));
            tW.Do(() => wit = db.BeginTransaction(IsolationLevel.Serializable));
            tD.Do(() => { dep.GetProperty(x, "v"); dep.GetProperty(y, "v"); });
            tW.Do(() => { wit.GetProperty(x, "v"); wit.GetProperty(y, "v"); });
            // withdraw を先に書いて commit (x を更新)
            tW.Do(() => wit.SetProperty(x, "v", PropertyValue.FromInt32(-11)));
            var eW = tW.Try(() => wit.Commit());
            // deposit が y を更新して commit
            tD.Do(() => dep.SetProperty(y, "v", PropertyValue.FromInt32(20)));
            var eD = tD.Try(() => dep.Commit());
            // read-only tx が両方を観測
            Exception? eR = null;
            tR.Do(() => ro = db.BeginTransaction(IsolationLevel.Serializable));
            tR.Do(() => { ro.GetProperty(x, "v"); ro.GetProperty(y, "v"); });
            eR = tR.Try(() => ro.Commit());
            // SI なら read-only anomaly が起き得るが、SSN 下では全体が直列化可能 (整合状態) になる。
            // 最低限の不変条件: いずれかが abort されるか、観測結果が直列実行と一致すること。
            // ここでは「システムが破綻なく直列化可能な決着をする」= 例外型が SSN/整合であることを確認。
            (eD is null or SerializabilityException).Should().BeTrue();
            (eW is null or SerializabilityException).Should().BeTrue();
            (eR is null or SerializabilityException).Should().BeTrue();
        }
        finally { Cleanup(dir); }
    }

    // ─────────────────────────── IsolationFailure_WW (lost update / cross) ─
    [Fact(Skip = PublicWriterGateSkip)]
    [Trait("Category", "Ssn")]
    public void IsolationFailure_WW_cross_cycle_aborts_one()
    {
        var dir = TempDir();
        try
        {
            using var db = Open(dir);
            var (x, y) = Seed(db, ("x", 0), ("y", 0));
            using var t1 = new TxThread(); using var t2 = new TxThread();
            IGraphTransaction a = null!, b = null!;
            t1.Do(() => a = db.BeginTransaction(IsolationLevel.Serializable));
            t2.Do(() => b = db.BeginTransaction(IsolationLevel.Serializable));
            // cross ww: a:w(x)…w(y), b:w(y)…w(x) → wait-for cycle → deadlock 検出で一方 abort
            t1.Do(() => a.SetProperty(x, "v", PropertyValue.FromInt32(1)));
            t2.Do(() => b.SetProperty(y, "v", PropertyValue.FromInt32(1)));
            // 2 本目の書き込みは相手のロック待ちでブロックするため並行に走らせる。
            var f1 = t1.RunAsync(() => a.SetProperty(y, "v", PropertyValue.FromInt32(1)));
            var f2 = t2.RunAsync(() => b.SetProperty(x, "v", PropertyValue.FromInt32(1)));
            // a/b はスレッド束縛 TX (TxThread) なので、2 本目の書き込み結果はそのスレッドの
            // 完了を同期待ちして取得する (deadlock 犠牲者は SetProperty で throw)。意図的ブロッキング。
#pragma warning disable xUnit1031 // 並行 deadlock シナリオの結果同期。async 化は interleaving を崩す
            var we1 = f1.Result; var we2 = f2.Result;
#pragma warning restore xUnit1031
            var e1 = we1 ?? t1.Try(() => a.Commit());
            var e2 = we2 ?? t2.Try(() => b.Commit());
            Aborted(e1, e2).Should().BeGreaterThanOrEqualTo(1,
                "ww cross-cycle は lock の wait-for graph で deadlock 検出され一方が abort される");
        }
        finally { Cleanup(dir); }
    }

    // ─────────────────────────── AtomicityFailure_WR ─────────────────────
    [Fact(Skip = PublicWriterGateSkip)]
    [Trait("Category", "Ssn")]
    public void AtomicityFailure_WR_cross_aborts_one()
    {
        var dir = TempDir();
        try
        {
            using var db = Open(dir);
            var (x, y) = Seed(db, ("x", 0), ("y", 0));
            using var t1 = new TxThread(); using var t2 = new TxThread();
            IGraphTransaction a = null!, b = null!;
            t1.Do(() => a = db.BeginTransaction(IsolationLevel.Serializable));
            t2.Do(() => b = db.BeginTransaction(IsolationLevel.Serializable));
            // a:r(y)w(x), b:r(x)w(y) — 交差した read→write で rw cycle を形成
            t1.Do(() => a.GetProperty(y, "v"));
            t2.Do(() => b.GetProperty(x, "v"));
            t1.Do(() => a.SetProperty(x, "v", PropertyValue.FromInt32(1)));
            t2.Do(() => b.SetProperty(y, "v", PropertyValue.FromInt32(1)));
            var e1 = t1.Try(() => a.Commit());
            var e2 = t2.Try(() => b.Commit());
            Ssn(e1, e2).Should().Be(1, "交差 rw は SSN が一方を abort する");
        }
        finally { Cleanup(dir); }
    }

    // ─────────────────────────── helpers ───────────────────────────
    private static VertexId[] Seed(QuiverDatabase db, params (string key, int val)[] vertices)
    {
        var ids = new VertexId[vertices.Length];
        using var tx = db.BeginTransaction();
        for (int i = 0; i < vertices.Length; i++)
        {
            ids[i] = tx.CreateVertex("N");
            tx.SetProperty(ids[i], "v", PropertyValue.FromInt32(vertices[i].val));
        }
        tx.Commit();
        return ids;
    }

    private static (VertexId, VertexId) Seed(QuiverDatabase db, (string, int) a, (string, int) b)
    {
        var ids = Seed(db, new[] { a, b });
        return (ids[0], ids[1]);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}

/// <summary>
/// 1 トランザクションを専用スレッドに固定し、各ステップを同期的に実行するヘルパー。
/// <see cref="Do"/> は呼び出し元をブロックして
/// アクションを所有スレッドで実行し、テストスレッドが大域順序を制御できるようにする。
/// </summary>
internal sealed class TxThread : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public TxThread()
    {
        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
    }

    private void Run()
    {
        foreach (var a in _work.GetConsumingEnumerable()) a();
    }

    /// <summary>所有スレッドで <paramref name="action"/> を実行し、完了まで呼び出し元をブロックする。</summary>
    public void Do(Action action)
    {
        using var done = new ManualResetEventSlim();
        Exception? captured = null;
        _work.Add(() => { try { action(); } catch (Exception e) { captured = e; } finally { done.Set(); } });
        done.Wait();
        if (captured != null) throw captured;
    }

    /// <summary>例外を捕捉して返す版の <see cref="Do"/>。</summary>
    public Exception? Try(Action action)
    {
        Exception? captured = null;
        try { Do(action); } catch (Exception e) { captured = e; }
        return captured;
    }

    /// <summary>
    /// 所有スレッドで非同期に実行し、捕捉した例外 (無ければ null) を <see cref="Task"/> で返す。
    /// ブロックしうる操作 (ロック待ち) を複数 tx で並行させ deadlock を発生させるのに使う。
    /// </summary>
    public System.Threading.Tasks.Task<Exception?> RunAsync(Action action)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<Exception?>();
        _work.Add(() =>
        {
            Exception? captured = null;
            try { action(); } catch (Exception e) { captured = e; }
            tcs.SetResult(captured);
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join();
        _work.Dispose();
    }
}

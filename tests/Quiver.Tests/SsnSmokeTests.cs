using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// SSN (Serial Safety Net) の簡易スモークテスト。
/// 本テストは以下を確認する:
/// <list type="bullet">
///   <item><see cref="IsolationLevel.Serializable"/> で典型的な write skew を起こすと、
///     一方が <see cref="SerializabilityException"/> で中断される。</item>
///   <item>Serializable を指定しない既定のスナップショット分離では両方がコミットする。</item>
///   <item>Edgeのトラバーサルによる読み取りも読み取り集合へ入り、
///     SSN が読み書き反依存を見落とさない。</item>
/// </list>
/// </summary>
[Collection("concurrency-stress")]
public sealed class SsnSmokeTests : IDisposable
{
    private const string PublicWriterGateSkip =
        "Multiple concurrent public writers are no longer supported; single-writer gate coverage replaces this white-box concurrency scenario.";

    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public SsnSmokeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_ssn_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), new QuiverDatabaseOptions
        {
            // 全面並列とカオステストによる CPU 過剰購読下では、コミット経路の内部ロック取得が
            // 超えて spurious な TransactionException("Lock timeout") を投げ、abort 集計を狂わせて
            // いた (SI では 0、Serializable では「SSN による 1 件」を期待する判定が壊れる)。寛大化して
            // starvation 由来の偽陽性を排除する。SSN abort 自体は barrier で snapshot 重複を強制して
            // いるため、timing ではなく決定的に発生する。
            LockTimeout = TimeSpan.FromSeconds(30),
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact(Skip = PublicWriterGateSkip)]
    public void Serializable_write_skew_via_direct_read_aborts_one_transaction()
    {
        var (a, b) = SeedTwoAccounts();

        // 古典的 write skew: T1 は B を読んで A に書き、T2 は A を読んで B に書く。
        // 両 tx の snapshot は重なる。SI なら両方 commit するが SSN は一方を abort する。
        var (ex1, ex2) = RunCrossSkew(
            IsolationLevel.Serializable,
            read1: tx => tx.GetProperty(b, "balance"),
            write1: tx => tx.SetProperty(a, "balance", PropertyValue.FromInt32(40)),
            read2: tx => tx.GetProperty(a, "balance"),
            write2: tx => tx.SetProperty(b, "balance", PropertyValue.FromInt32(40)));

        CountSerializabilityFailures(ex1, ex2).Should().Be(1,
            "SSN は write skew の片方を abort し、もう片方は commit させる");
    }

    [Fact(Skip = PublicWriterGateSkip)]
    public void SnapshotIsolation_write_skew_allows_both_commits()
    {
        var (a, b) = SeedTwoAccounts();

        // 既定 (SI) は SSN 検証が走らないため、同じ write skew でも両方 commit する (回帰確認)。
        var (ex1, ex2) = RunCrossSkew(
            IsolationLevel.SnapshotIsolation,
            read1: tx => tx.GetProperty(b, "balance"),
            write1: tx => tx.SetProperty(a, "balance", PropertyValue.FromInt32(40)),
            read2: tx => tx.GetProperty(a, "balance"),
            write2: tx => tx.SetProperty(b, "balance", PropertyValue.FromInt32(40)));

        // 失敗時にロックタイムアウトによるスターベーションか、直列化例外の誤検出かを
        // 分かるよう例外型とメッセージをダンプする。SI では SSN 検証が走らないため、両方 commit が
        // timing 非依存の正しい挙動。
        ex1.Should().BeNull("SI では write skew でも両方 commit する (T1 が {0} で abort された)", Describe(ex1));
        ex2.Should().BeNull("SI では write skew でも両方 commit する (T2 が {0} で abort された)", Describe(ex2));
    }

    private static string Describe(Exception? ex)
        => ex is null ? "(none)" : $"{ex.GetType().Name}: {ex.Message}";

    [Fact(Skip = PublicWriterGateSkip)]
    public void Serializable_write_skew_via_edge_traversal_aborts_one_transaction()
    {
        // 読み取りを「直接 Read」ではなく「edge traversal」で行う write skew。
        // T1 は A の隣接を走査して (= edge eA を読む) eB を書き換え、
        // T2 は B の隣接を走査して (= edge eB を読む) eA を書き換える。
        // rw 交差: T1 が eA を読み T2 が eA を書く / T2 が eB を読み T1 が eB を書く → cycle。
        // 走査経由の read が read-set に入らなければ SSN は検出できず両方 commit してしまう。
        // これが abort されることで、read 捕捉が直接 Read 経路に限定されていないことを保証する。
        EdgeId eA, eB;
        using (var tx = _db.BeginTransaction())
        {
            var a = tx.CreateVertex("Account");
            var b = tx.CreateVertex("Account");
            eA = tx.CreateEdge(a, a, "SELF");
            eB = tx.CreateEdge(b, b, "SELF");
            tx.SetProperty(eA, "flag", PropertyValue.FromInt32(0));
            tx.SetProperty(eB, "flag", PropertyValue.FromInt32(0));
            tx.Commit();
            _a = a; _b = b;
        }

        var (ex1, ex2) = RunCrossSkew(
            IsolationLevel.Serializable,
            read1: tx => DrainNeighbors(tx, _a),
            write1: tx => tx.SetProperty(eB, "flag", PropertyValue.FromInt32(1)),
            read2: tx => DrainNeighbors(tx, _b),
            write2: tx => tx.SetProperty(eA, "flag", PropertyValue.FromInt32(1)));

        CountSerializabilityFailures(ex1, ex2).Should().Be(1,
            "traversal 経由の read も read-set に入るため SSN は rw cycle を検出して一方を abort する");
    }

    [Fact]
    public void Serializable_commit_stamp_clock_survives_restart()
    {
        // コミットスタンプのクロックは再起動をまたいで単調増加する。そうでなければ再起動後に
        // クロックが 0 に戻り、永続化済みの大きい Pstamp (旧空間) と小さい c(T) (新空間) が
        // 混在して、競合の無い上書きまで false-abort してしまう。
        var dir = Path.Combine(Path.GetTempPath(), "quiver_ssn_restart_" + Guid.NewGuid().ToString("N"));
        try
        {
            VertexId x;
            // Serializable の読み取りトランザクションを繰り返して X.Pstamp とクロックを進める。
            using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
            {
                using (var tx = db.BeginTransaction())
                {
                    x = tx.CreateVertex("N");
                    tx.SetProperty(x, "v", PropertyValue.FromInt32(1));
                    tx.Commit();
                }
                for (int i = 0; i < 10; i++)
                {
                    using var rtx = db.BeginTransaction(IsolationLevel.Serializable);
                    _ = rtx.GetProperty(x, "v"); // read → post-commit で X.Pstamp = c(rtx)
                    rtx.Commit();
                }
            }

            // 再オープンし、競合のない単独の Serializable トランザクションで X を上書きする。
            // クロックが連続していれば c(T) > X.Pstamp となり false-abort しない。
            using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
            {
                using var wtx = db.BeginTransaction(IsolationLevel.Serializable);
                wtx.SetProperty(x, "v", PropertyValue.FromInt32(2));
                Action commit = () => wtx.Commit();
                commit.Should().NotThrow<SerializabilityException>(
                    "再起動後も commit-stamp クロックが連続するので、競合の無い上書きが false-abort しない");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private VertexId _a, _b;

    private static void DrainNeighbors(IGraphTransaction tx, VertexId vertexId)
    {
        var e = tx.EnumerateEdges(vertexId);
        while (e.MoveNext()) { _ = e.Current.Type; } // 列挙が内部で各 edge を Read する
    }

    private static int CountSerializabilityFailures(Exception? ex1, Exception? ex2)
        => new[] { ex1, ex2 }.Count(e => e is SerializabilityException);

    private (VertexId a, VertexId b) SeedTwoAccounts()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateVertex("Account");
        var b = tx.CreateVertex("Account");
        tx.SetProperty(a, "balance", PropertyValue.FromInt32(100));
        tx.SetProperty(b, "balance", PropertyValue.FromInt32(100));
        tx.Commit();
        return (a, b);
    }

    /// <summary>
    /// 2 スレッドで対称な交差競合を実行する。各 tx は read → write の順で、相手と交差する
    /// entity を触る。CountdownEvent で (1) 両方 begin 後に read、(2) 両方 read 後に
    /// write/commit、の順序を強制して snapshot を確実に重ねる。
    /// </summary>
    private (Exception? t1, Exception? t2) RunCrossSkew(
        IsolationLevel level,
        Action<IGraphTransaction> read1, Action<IGraphTransaction> write1,
        Action<IGraphTransaction> read2, Action<IGraphTransaction> write2)
    {
        using var bothBegun = new CountdownEvent(2);
        using var bothRead = new CountdownEvent(2);

        Exception? Body(Action<IGraphTransaction> read, Action<IGraphTransaction> write)
        {
            try
            {
                using var tx = _db.BeginTransaction(level);
                bothBegun.Signal();
                bothBegun.Wait();

                read(tx);
                bothRead.Signal();
                bothRead.Wait();

                write(tx);
                tx.Commit();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        Exception? ex1 = null, ex2 = null;
        var th1 = new Thread(() => ex1 = Body(read1, write1));
        var th2 = new Thread(() => ex2 = Body(read2, write2));
        th1.Start();
        th2.Start();
        th1.Join();
        th2.Join();
        return (ex1, ex2);
    }
}

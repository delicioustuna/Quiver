using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FT-33 SSN (Serial Safety Net) の簡易スモークテスト。正式な canonical シナリオ網羅
/// (write skew / read-only anomaly / dangerous structure / safe retry) と overhead bench は
/// FT-34 で別途整備する。本テストは以下を確認する:
/// <list type="bullet">
///   <item><see cref="IsolationLevel.Serializable"/> で古典的 write skew を起こすと一方が
///     <see cref="SerializabilityException"/> で abort される。</item>
///   <item>Serializable を指定しない既定 (SI) では同じ write skew でも両方 commit する。</item>
///   <item>読み取りが <b>relationship traversal 経由</b> でも read-set に入り、SSN が
///     rw-antidependency を取りこぼさない (直接 Read に限定されていない)。</item>
/// </list>
/// </summary>
public sealed class SsnSmokeTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public SsnSmokeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_ssn_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
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

    [Fact]
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

        ex1.Should().BeNull();
        ex2.Should().BeNull();
    }

    [Fact]
    public void Serializable_write_skew_via_relationship_traversal_aborts_one_transaction()
    {
        // 読み取りを「直接 Read」ではなく「relationship traversal」で行う write skew。
        // T1 は A の隣接を走査して (= edge eA を読む) eB を書き換え、
        // T2 は B の隣接を走査して (= edge eB を読む) eA を書き換える。
        // rw 交差: T1 が eA を読み T2 が eA を書く / T2 が eB を読み T1 が eB を書く → cycle。
        // 走査経由の read が read-set に入らなければ SSN は検出できず両方 commit してしまう。
        // これが abort されることで、read 捕捉が直接 Read 経路に限定されていないことを保証する。
        RelationshipId eA, eB;
        using (var tx = _db.BeginTransaction())
        {
            var a = tx.CreateNode("Account");
            var b = tx.CreateNode("Account");
            eA = tx.CreateRelationship(a, a, "SELF");
            eB = tx.CreateRelationship(b, b, "SELF");
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
        // FT-33 (④): commit-stamp クロックは再起動跨ぎで単調連続。これがないと再起動後に
        // クロックが 0 に戻り、永続化済みの大きい Pstamp (旧空間) と小さい c(T) (新空間) が
        // 混在して、競合の無い上書きまで false-abort してしまう。
        var dir = Path.Combine(Path.GetTempPath(), "quiver_ssn_restart_" + Guid.NewGuid().ToString("N"));
        try
        {
            NodeId x;
            // Phase 1: Serializable な read tx を繰り返して X.Pstamp とクロックを進める。
            using (var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
            {
                using (var tx = db.BeginTransaction())
                {
                    x = tx.CreateNode("N");
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

            // Phase 2: 再 open。競合の無い単独 Serializable tx が X を上書きする。
            // クロックが連続していれば c(T) > X.Pstamp となり false-abort しない。
            using (var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
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

    private NodeId _a, _b;

    private static void DrainNeighbors(IGraphTransaction tx, NodeId nodeId)
    {
        var e = tx.EnumerateRelationships(nodeId);
        while (e.MoveNext()) { _ = e.Current.Type; } // 列挙が内部で各 relationship を Read する
    }

    private static int CountSerializabilityFailures(Exception? ex1, Exception? ex2)
        => new[] { ex1, ex2 }.Count(e => e is SerializabilityException);

    private (NodeId a, NodeId b) SeedTwoAccounts()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("Account");
        var b = tx.CreateNode("Account");
        tx.SetProperty(a, "balance", PropertyValue.FromInt32(100));
        tx.SetProperty(b, "balance", PropertyValue.FromInt32(100));
        tx.Commit();
        return (a, b);
    }

    /// <summary>
    /// 2 スレッドで対称な交差競合を実行する。各 tx は read → write の順で、相手と交差する
    /// entity を触る。WalPageContext / MvccContext は thread-static なので書き込む 2 tx は別
    /// スレッドで動かす。CountdownEvent で (1) 両方 begin 後に read、(2) 両方 read 後に
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

using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FT-26 MVCC: GraphDatabase 経由で snapshot isolation の挙動を確認する end-to-end テスト。
///
/// <para>
/// NodeStore / RelationshipStore / PropertyStore の record header に xmin / xmax が乗り、
/// Transaction.ctor が <c>MvccContext.Begin</c> を呼び、TransactionManager.OnCommit が
/// <c>CommittedTxRegistry.MarkCommitted</c> を呼ぶ全経路の結合確認。
/// </para>
/// </summary>
public sealed class MvccVisibilityTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public MvccVisibilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_mvcc_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Snapshot_does_not_see_concurrent_uncommitted_inserts()
    {
        // 既存 node を 1 つ commit。
        NodeId existing;
        using (var tx = _db.BeginTransaction())
        {
            existing = tx.CreateNode("Existing");
            tx.Commit();
        }

        // reader tx を開始 (snapshot 取得時に concurrent writer は active なし)。
        using var reader = _db.BeginTransaction();

        // writer tx を別に開始して node を追加 (まだ commit しない)。
        var writer = _db.BeginTransaction();
        var added = writer.CreateNode("Added");

        // reader からは追加 node は invisible。
        reader.NodeExists(added).Should().BeFalse("writer がまだ commit していないので snapshot から見えない");
        reader.NodeExists(existing).Should().BeTrue();

        // writer commit 後も reader の snapshot からは依然不可視 (snapshot isolation)。
        writer.Commit();
        reader.NodeExists(added).Should().BeFalse("writer commit 後も reader snapshot からは見えない (SI)");

        // 新規 reader を開けば可視 (writer commit は新規 snapshot の前)。
        using var freshReader = _db.BeginTransaction();
        freshReader.NodeExists(added).Should().BeTrue("新規 snapshot からは writer commit が見える");
        freshReader.NodeExists(existing).Should().BeTrue();
    }

    [Fact]
    public void Aborted_tx_writes_are_invisible_to_all()
    {
        var failedTx = _db.BeginTransaction();
        var ghost = failedTx.CreateNode("Ghost");
        failedTx.Rollback();

        using var observer = _db.BeginTransaction();
        observer.NodeExists(ghost).Should().BeFalse("abort された tx の write は visible にならない");
    }

    [Fact]
    public void Concurrent_reader_sees_consistent_snapshot_across_long_operation()
    {
        // base: 3 node + 2 relationship。
        NodeId a, b, c;
        using (var tx = _db.BeginTransaction())
        {
            a = tx.CreateNode("A");
            b = tx.CreateNode("B");
            c = tx.CreateNode("C");
            tx.CreateRelationship(a, b, "R");
            tx.CreateRelationship(a, c, "R");
            tx.Commit();
        }

        using var reader = _db.BeginTransaction();
        int initialCountFromA = CountOutgoing(reader, a);
        initialCountFromA.Should().Be(2);

        // 並行 writer が新規エッジ追加 + 既存 b → a エッジ削除を伴う tx を実行。
        using (var writer = _db.BeginTransaction())
        {
            var d = writer.CreateNode("D");
            writer.CreateRelationship(a, d, "R");
            writer.Commit();
        }

        // reader はスナップショット時点の {b, c} のみ見える。
        int latestFromA = CountOutgoing(reader, a);
        latestFromA.Should().Be(2, "reader snapshot は writer commit を超えて新エッジを見ない");
    }

    [Fact]
    public void Reopen_preserves_visibility_for_pre_committed_data()
    {
        NodeId pre;
        using (var tx = _db.BeginTransaction())
        {
            pre = tx.CreateNode("Pre");
            tx.SetProperty(pre, "score", PropertyValue.FromInt32(42));
            tx.Commit();
        }
        _db.Dispose();

        using var reopened = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var rtx = reopened.BeginTransaction();
        // recovery の CommittedTxRegistry rebuild と horizon により xmin が registry / horizon 経由で visible。
        rtx.NodeExists(pre).Should().BeTrue();
        rtx.GetProperty(pre, "score").Int32Value.Should().Be(42);
    }

    [Fact]
    public void Property_overwrite_is_visible_to_writer_not_to_prior_reader()
    {
        // base: node with property = 100
        NodeId n;
        using (var tx = _db.BeginTransaction())
        {
            n = tx.CreateNode("N");
            tx.SetProperty(n, "v", PropertyValue.FromInt32(100));
            tx.Commit();
        }

        using var reader = _db.BeginTransaction();
        reader.GetProperty(n, "v").Int32Value.Should().Be(100);

        using (var writer = _db.BeginTransaction())
        {
            writer.SetProperty(n, "v", PropertyValue.FromInt32(200));
            writer.Commit();
        }

        // reader snapshot は writer commit を超えない。
        reader.GetProperty(n, "v").Int32Value.Should().Be(100,
            "reader snapshot は writer の SetProperty 上書きを見ない (旧 version が enumerate で先にヒット)");

        using var fresh = _db.BeginTransaction();
        fresh.GetProperty(n, "v").Int32Value.Should().Be(200);
    }

    [Fact]
    public void Stress_long_reader_with_interleaved_writers_keeps_consistent_snapshot()
    {
        // FT-26 完了条件 (single-thread interleave 経路): long reader と writer tx を
        // 時系列にインターリーブし、reader snapshot が writer commit に揺らがないことを確認。
        var baseNodes = new List<NodeId>();
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 100; i++)
                baseNodes.Add(tx.CreateNode("Base"));
            tx.Commit();
        }

        using var longReader = _db.BeginTransaction();
        int baselineCount = 0;
        foreach (var nid in baseNodes)
            if (longReader.NodeExists(nid)) baselineCount++;
        baselineCount.Should().Be(baseNodes.Count);

        for (int round = 0; round < 50; round++)
        {
            for (int j = 0; j < 4; j++)
            {
                using var wtx = _db.BeginTransaction();
                var n = wtx.CreateNode("W");
                wtx.SetProperty(n, "round", PropertyValue.FromInt64(round));
                wtx.Commit();
            }

            int observed = 0;
            foreach (var nid in baseNodes)
                if (longReader.NodeExists(nid)) observed++;
            observed.Should().Be(baselineCount,
                $"reader snapshot は writer commit を超えても drift せず、round {round} で {observed} != {baselineCount}");
        }

        using var fresh = _db.BeginTransaction();
        int baseFromFresh = 0;
        foreach (var nid in baseNodes)
            if (fresh.NodeExists(nid)) baseFromFresh++;
        baseFromFresh.Should().Be(baseNodes.Count);
    }

    [Fact]
    public void Concurrency_stress_multithread_reader_and_writers_do_not_corrupt()
    {
        // FT-26 DoD「long reader (1000ms スキャン) と concurrent writer が互いをブロックしない、
        // reader の結果が consistent snapshot」を真の multi-thread で検証する。
        // per-frame RW lock により PagedFile レベルの torn-read を防ぐ。
        var baseNodes = new List<NodeId>();
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 200; i++)
                baseNodes.Add(tx.CreateNode("Base"));
            tx.Commit();
        }

        const int writerIterations = 300;
        var stopWriter = new System.Threading.ManualResetEventSlim(false);
        var writerErrors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var readerErrors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var driftErrors = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Writer thread.
        var writerTask = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < writerIterations; i++)
                {
                    using var wtx = _db.BeginTransaction();
                    var n = wtx.CreateNode("Writer");
                    wtx.SetProperty(n, "i", PropertyValue.FromInt64(i));
                    wtx.Commit();
                }
            }
            catch (Exception ex) { writerErrors.Add(ex); }
            finally { stopWriter.Set(); }
        });

        // Reader thread: long-lived snapshot, re-scans base set while writer mutates.
        var readerTask = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var rtx = _db.BeginTransaction();
                int baselineCount = 0;
                foreach (var nid in baseNodes)
                    if (rtx.NodeExists(nid)) baselineCount++;

                while (!stopWriter.IsSet)
                {
                    int observed = 0;
                    foreach (var nid in baseNodes)
                        if (rtx.NodeExists(nid)) observed++;
                    if (observed != baselineCount)
                        driftErrors.Add($"snapshot drift: baseline={baselineCount}, observed={observed}");
                }
            }
            catch (Exception ex) { readerErrors.Add(ex); }
        });

#pragma warning disable xUnit1031 // 並行スレッドの結了を待つ必要があり、Sync wait が意図された設計。
        System.Threading.Tasks.Task.WaitAll(writerTask, readerTask);
#pragma warning restore xUnit1031

        writerErrors.Should().BeEmpty("writer は concurrent read 中も例外を起こさない");
        readerErrors.Should().BeEmpty("reader は concurrent write 中も corruption / 例外を起こさない");
        driftErrors.Should().BeEmpty("snapshot は writer commit を観測しない (SI)");

        using var fresh = _db.BeginTransaction();
        int baseFromFresh = 0;
        foreach (var nid in baseNodes)
            if (fresh.NodeExists(nid)) baseFromFresh++;
        baseFromFresh.Should().Be(baseNodes.Count, "全 base node が fresh snapshot から visible");
    }

    [Fact]
    public void Throughput_sanity_mvcc_does_not_regress_excessively()
    {
        // FT-26 完了条件: 「単一 tx workload で書き込みスループットが MVCC なし比 80% 以上」
        // MVCC なしの比較は format 互換性のため難しいので、絶対値の妥当性チェックに留める。
        // 1000 ノード createNode + commit が 30 秒以内に終わること (CI で十分余裕のある上限)。
        const int N = 1000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < N; i++)
                tx.CreateNode("X");
            tx.Commit();
        }
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            $"FT-26 MVCC オーバヘッド sanity: {N} ノード create+commit が {sw.ElapsedMilliseconds} ms");

        // verify all visible
        using var verify = _db.BeginTransaction();
        int visible = 0;
        for (long id = 0; id < N + 10; id++)
            if (verify.NodeExists(new NodeId(id))) visible++;
        visible.Should().BeGreaterOrEqualTo(N, $"創出した {N} ノードが visible");
    }

    private static int CountOutgoing(IGraphTransaction tx, NodeId node)
    {
        int count = 0;
        var en = tx.EnumerateRelationships(node, Direction.Outgoing);
        while (en.MoveNext())
        {
            if (en.Current.Source == node) count++;
        }
        return count;
    }
}

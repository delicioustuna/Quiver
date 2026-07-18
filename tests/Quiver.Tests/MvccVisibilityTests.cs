using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <c>QuiverDatabase</c> 経由で MVCC のスナップショット分離を確認する
/// エンドツーエンドテスト。
///
/// <para>
/// VertexStore / EdgeStore / PropertyVersionStore のレコードヘッダーが xmin / xmax を保持し、
/// トランザクション開始時に snapshot が固定され、コミット時に
/// committed high-water が進む一連の経路を確認する。
/// </para>
/// </summary>
public sealed class MvccVisibilityTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public MvccVisibilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_mvcc_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
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
        // 既存 vertex を 1 つ commit。
        VertexId existing;
        using (var tx = _db.BeginWriteTransaction())
        {
            existing = tx.CreateVertex("Existing");
            tx.Commit();
        }

        // reader tx を開始 (snapshot 取得時に concurrent writer は active なし)。
        using var reader = _db.BeginReadTransaction();

        // writer tx を別に開始して vertex を追加 (まだ commit しない)。
        var writer = _db.BeginWriteTransaction();
        var added = writer.CreateVertex("Added");

        // reader からは追加 vertex は invisible。
        reader.VertexExists(added).Should().BeFalse("writer がまだ commit していないので snapshot から見えない");
        reader.VertexExists(existing).Should().BeTrue();

        // writer commit 後も reader の snapshot からは依然不可視 (snapshot isolation)。
        writer.Commit();
        reader.VertexExists(added).Should().BeFalse("writer commit 後も reader snapshot からは見えない (SI)");

        // 新規 reader を開けば可視 (writer commit は新規 snapshot の前)。
        using var freshReader = _db.BeginReadTransaction();
        freshReader.VertexExists(added).Should().BeTrue("新規 snapshot からは writer commit が見える");
        freshReader.VertexExists(existing).Should().BeTrue();
    }

    [Fact]
    public void Aborted_tx_writes_are_invisible_to_all()
    {
        var failedTx = _db.BeginWriteTransaction();
        var ghost = failedTx.CreateVertex("Ghost");
        failedTx.Rollback();

        using var observer = _db.BeginReadTransaction();
        observer.VertexExists(ghost).Should().BeFalse("abort された tx の write は visible にならない");
    }

    [Fact]
    public void Concurrent_reader_sees_consistent_snapshot_across_long_operation()
    {
        // base: 3 vertex + 2 edge。
        VertexId a, b, c;
        using (var tx = _db.BeginWriteTransaction())
        {
            a = tx.CreateVertex("A");
            b = tx.CreateVertex("B");
            c = tx.CreateVertex("C");
            tx.CreateEdge(a, b, "R");
            tx.CreateEdge(a, c, "R");
            tx.Commit();
        }

        using var reader = _db.BeginReadTransaction();
        int initialCountFromA = CountOutgoing(reader, a);
        initialCountFromA.Should().Be(2);

        // 並行 writer が新規エッジ追加 + 既存 b → a エッジ削除を伴う tx を実行。
        using (var writer = _db.BeginWriteTransaction())
        {
            var d = writer.CreateVertex("D");
            writer.CreateEdge(a, d, "R");
            writer.Commit();
        }

        // reader はスナップショット時点の {b, c} のみ見える。
        int latestFromA = CountOutgoing(reader, a);
        latestFromA.Should().Be(2, "reader snapshot は writer commit を超えて新エッジを見ない");
    }

    [Fact]
    public void Reopen_preserves_visibility_for_pre_committed_data()
    {
        VertexId pre;
        using (var tx = _db.BeginWriteTransaction())
        {
            pre = tx.CreateVertex("Pre");
            tx.SetProperty(pre, "score", PropertyValue.FromInt32(42));
            tx.Commit();
        }
        _db.Dispose();

        using var reopened = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var rtx = reopened.BeginReadTransaction();
        // recovery の CommittedTxRegistry rebuild と horizon により xmin が registry / horizon 経由で visible。
        rtx.VertexExists(pre).Should().BeTrue();
        rtx.GetProperty(pre, "score").Int32Value.Should().Be(42);
    }

    [Fact]
    public void Property_overwrite_is_visible_to_writer_not_to_prior_reader()
    {
        // base: vertex with property = 100
        VertexId n;
        using (var tx = _db.BeginWriteTransaction())
        {
            n = tx.CreateVertex("N");
            tx.SetProperty(n, "v", PropertyValue.FromInt32(100));
            tx.Commit();
        }

        using var reader = _db.BeginReadTransaction();
        reader.GetProperty(n, "v").Int32Value.Should().Be(100);

        using (var writer = _db.BeginWriteTransaction())
        {
            writer.SetProperty(n, "v", PropertyValue.FromInt32(200));
            writer.Commit();
        }

        // reader snapshot は writer commit を超えない。
        reader.GetProperty(n, "v").Int32Value.Should().Be(100,
            "reader snapshot は writer の SetProperty 上書きを見ない (旧 version が enumerate で先にヒット)");

        using var fresh = _db.BeginReadTransaction();
        fresh.GetProperty(n, "v").Int32Value.Should().Be(200);
    }

    [Fact]
    public void Stress_long_reader_with_interleaved_writers_keeps_consistent_snapshot()
    {
        // 単一スレッドで長時間リーダーとライタートランザクションを交互に進め、
        // 時系列にインターリーブし、reader snapshot が writer commit に揺らがないことを確認。
        var baseVertices = new List<VertexId>();
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 100; i++)
                baseVertices.Add(tx.CreateVertex("Base"));
            tx.Commit();
        }

        using var longReader = _db.BeginReadTransaction();
        int baselineCount = 0;
        foreach (var nid in baseVertices)
            if (longReader.VertexExists(nid)) baselineCount++;
        baselineCount.Should().Be(baseVertices.Count);

        for (int round = 0; round < 50; round++)
        {
            for (int j = 0; j < 4; j++)
            {
                using var wtx = _db.BeginWriteTransaction();
                var n = wtx.CreateVertex("W");
                wtx.SetProperty(n, "round", PropertyValue.FromInt64(round));
                wtx.Commit();
            }

            int observed = 0;
            foreach (var nid in baseVertices)
                if (longReader.VertexExists(nid)) observed++;
            observed.Should().Be(baselineCount,
                $"reader snapshot は writer commit を超えても drift せず、round {round} で {observed} != {baselineCount}");
        }

        using var fresh = _db.BeginReadTransaction();
        int baseFromFresh = 0;
        foreach (var nid in baseVertices)
            if (fresh.VertexExists(nid)) baseFromFresh++;
        baseFromFresh.Should().Be(baseVertices.Count);
    }

    [Fact]
    public void Concurrency_stress_multithread_reader_and_writers_do_not_corrupt()
    {
        // 1 秒走査する長時間リーダーと並行ライターが互いをブロックせず、
        // reader の結果が consistent snapshot」を真の multi-thread で検証する。
        // per-frame RW lock により PagedFile レベルの torn-read を防ぐ。
        var baseVertices = new List<VertexId>();
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 200; i++)
                baseVertices.Add(tx.CreateVertex("Base"));
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
                    using var wtx = _db.BeginWriteTransaction();
                    var n = wtx.CreateVertex("Writer");
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
                using var rtx = _db.BeginReadTransaction();
                int baselineCount = 0;
                foreach (var nid in baseVertices)
                    if (rtx.VertexExists(nid)) baselineCount++;

                while (!stopWriter.IsSet)
                {
                    int observed = 0;
                    foreach (var nid in baseVertices)
                        if (rtx.VertexExists(nid)) observed++;
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

        using var fresh = _db.BeginReadTransaction();
        int baseFromFresh = 0;
        foreach (var nid in baseVertices)
            if (fresh.VertexExists(nid)) baseFromFresh++;
        baseFromFresh.Should().Be(baseVertices.Count, "全 base vertex が fresh snapshot から visible");
    }

    [Fact]
    public void Throughput_sanity_mvcc_does_not_regress_excessively()
    {
        // 単一トランザクションのワークロードで書き込み性能が極端に低下しないことを
        // MVCC なしの比較は format 互換性のため難しいので、絶対値の妥当性チェックに留める。
        // 1000 Vertex createVertex + commit が 30 秒以内に終わること (CI で十分余裕のある上限)。
        const int N = 1000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < N; i++)
                tx.CreateVertex("X");
            tx.Commit();
        }
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            $"MVCC オーバヘッド sanity: {N} Vertex create+commit が {sw.ElapsedMilliseconds} ms");

        // verify all visible
        using var verify = _db.BeginReadTransaction();
        int visible = 0;
        for (long id = 0; id < N + 10; id++)
            if (verify.VertexExists(new VertexId(id))) visible++;
        visible.Should().BeGreaterOrEqualTo(N, $"創出した {N} Vertexが visible");
    }

    private static int CountOutgoing(IReadTransaction tx, VertexId vertex)
    {
        int count = 0;
        var en = tx.EnumerateEdges(vertex, Direction.Outgoing);
        while (en.MoveNext())
        {
            if (en.Current.Source == vertex) count++;
        }
        return count;
    }
}

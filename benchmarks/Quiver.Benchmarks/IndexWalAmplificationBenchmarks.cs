using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// 索引を QUIVER-SW page-WAL の対象にした場合の WAL 書き込み増幅と
/// throughput 影響の実測。
///
/// 計測軸:
/// - 1 tx に大量 insert を詰める **bulk** パス (commit-coalesce の best case)
/// - 各 insert を独立 tx で行う **per-tx** パス (fsync per commit の worst case)
/// - 同一ホットページを上書きする **hot-page** パス (per-tx PageImage 重複の上限)
///
/// 各シナリオで:
/// - <c>WalBytes</c>: 完了後の WAL ディレクトリ合計サイズ (実物理 amplification)
/// - <c>WalBytesPerEntry</c>: 索引 entry 1 件あたりの WAL バイト数
///
/// MemoryDiagnoser で in-process アロケーションも併せて取る。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Monitoring,
    launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class IndexWalAmplificationBenchmarks
{
    [Params(1_000, 10_000)]
    public int EntryCount { get; set; }

    private string _dbPath = null!;
    private QuiverDatabase _db = null!;

    [IterationSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("ft20_wal_amp");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _ = _db.Schema.GetOrCreateLabel("Doc");
        _ = _db.Schema.GetOrCreatePropertyKey("idx");
        _db.Schema.CreateIndex("idx_bench", "Doc", "idx", IndexKind.Int64Equality);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        BenchTempDir.Delete(_dbPath);
    }

    /// <summary>
    /// bulk: 1 トランザクションに全 insert を詰めて 1 回だけ commit。
    /// commit-coalesce で同一ページの複数変更が 1 PageImage に集約される best case。
    /// </summary>
    [Benchmark(Description = "bulk (single tx)")]
    public long BulkSingleTx()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < EntryCount; i++)
            {
                var vertex = tx.CreateVertex("Doc");
                tx.IndexInsert("idx_bench", (long)i, vertex);
            }
            tx.Commit();
        }
        return WalBytes();
    }

    /// <summary>
    /// per-tx: 各 insert を独立 tx で行う。worst case (PageImage の coalesce 効果ゼロ、
    /// commit fsync が per-entry に発生)。実用シナリオでは batching が推奨されることを示す参考値。
    /// </summary>
    [Benchmark(Description = "per-tx (one insert per tx)")]
    public long PerTransaction()
    {
        for (int i = 0; i < EntryCount; i++)
        {
            using var tx = _db.BeginTransaction();
            var vertex = tx.CreateVertex("Doc");
            tx.IndexInsert("idx_bench", (long)i, vertex);
            tx.Commit();
        }
        return WalBytes();
    }

    /// <summary>
    /// hot-page: 100 件だけの key を使い回し、同じ leaf ページに延々と insert する。
    /// per-tx PageImage が同一ページの繰り返し write になるため、PageImage の絶対値は
    /// 「ページ数 × commit 回数 × ページサイズ」で頭打ちになることを確認する。
    /// </summary>
    [Benchmark(Description = "hot-page (per-tx, 100 keys recycled)")]
    public long HotPagePerTx()
    {
        var vertices = new VertexId[100];
        // 100 Vertexを 1 tx で先行作成 (key と 1:1 対応)。
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = tx.CreateVertex("Doc");
            tx.Commit();
        }
        // EntryCount 回、同じ 100 key を順繰りに index insert (各回独立 tx)。
        for (int i = 0; i < EntryCount; i++)
        {
            using var tx = _db.BeginTransaction();
            long key = (long)(i % vertices.Length);
            tx.IndexInsert("idx_bench", key, vertices[(int)key]);
            tx.Commit();
        }
        return WalBytes();
    }

    private long WalBytes()
    {
        var walFile = Path.Combine(_dbPath, "graph.quiver-wal");
        if (!File.Exists(walFile)) return 0;
        try { return new FileInfo(walFile).Length; } catch { return 0; }
    }
}

/// <summary>
/// 一回限りの「対比計測」用スタンドアロンランナー (BenchmarkDotNet 経由ではなく
/// 通常の Stopwatch 測定)。CI / ローカル開発で素早く参考値を取るために使う。
/// 詳細プロファイリングは <see cref="IndexWalAmplificationBenchmarks"/> で取得。
/// </summary>
public static class IndexWalAmplificationStandalone
{
    public record Result(string Scenario, int EntryCount, long WalBytes, double WallMs);

    public static Result Run(string scenario, int entryCount)
    {
        var dbPath = BenchTempDir.Create("ft20_standalone");
        try
        {
            // 並列シナリオは group commit window を opt-in にして coalesce 窓を広げる。
            var options = scenario == "per-tx-parallel"
                ? new QuiverDatabaseOptions { GroupCommitWindow = TimeSpan.FromMicroseconds(100) }
                : new QuiverDatabaseOptions();

            using var db = QuiverDatabase.Open(System.IO.Path.Combine(dbPath, "graph.quiver"), options);
            _ = db.Schema.GetOrCreateLabel("Doc");
            _ = db.Schema.GetOrCreatePropertyKey("idx");
            db.Schema.CreateIndex("idx_bench", "Doc", "idx", IndexKind.Int64Equality);

            var sw = Stopwatch.StartNew();
            switch (scenario)
            {
                case "bulk":
                {
                    using var tx = db.BeginTransaction();
                    for (int i = 0; i < entryCount; i++)
                    {
                        var vertex = tx.CreateVertex("Doc");
                        tx.IndexInsert("idx_bench", (long)i, vertex);
                    }
                    tx.Commit();
                    break;
                }
                case "per-tx":
                {
                    for (int i = 0; i < entryCount; i++)
                    {
                        using var tx = db.BeginTransaction();
                        var vertex = tx.CreateVertex("Doc");
                        tx.IndexInsert("idx_bench", (long)i, vertex);
                        tx.Commit();
                    }
                    break;
                }
                case "per-tx-parallel":
                {
                    // 並列 per-tx insert で cross-tx coalescing 効果を測定。
                    // group commit window を opt-in (100µs) して flush ループ内で
                    // 複数 tx の PageImage を 1 drain に集約させる。
                    int threadCount = Math.Max(8, Environment.ProcessorCount);
                    int perThread = entryCount / threadCount;
                    var threads = new Thread[threadCount];
                    for (int t = 0; t < threadCount; t++)
                    {
                        int tid = t;
                        threads[t] = new Thread(() =>
                        {
                            for (int i = 0; i < perThread; i++)
                            {
                                using var tx = db.BeginTransaction();
                                var vertex = tx.CreateVertex("Doc");
                                tx.IndexInsert("idx_bench", tid * 1_000_000L + i, vertex);
                                tx.Commit();
                            }
                        });
                    }
                    foreach (var th in threads) th.Start();
                    foreach (var th in threads) th.Join();
                    break;
                }
                default: throw new ArgumentException($"Unknown scenario: {scenario}");
            }
            sw.Stop();

            long walBytes = 0;
            var walFile = Path.Combine(dbPath, "graph.quiver-wal");
            if (File.Exists(walFile))
                try { walBytes = new FileInfo(walFile).Length; } catch { }

            return new Result(scenario, entryCount, walBytes, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            BenchTempDir.Delete(dbPath);
        }
    }
}

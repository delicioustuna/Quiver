using System.Diagnostics;
using Quiver.Core;
using Quiver.Storage.Wal;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// FT-27: WAL group commit batching の throughput 改善を BDN 無しで短時間計測するランナー。
///
/// 64 concurrent producer が 1 commit ずつ Append + FlushTo を回し、
/// <see cref="GraphDatabaseOptions.GroupCommitWindow"/> = 0 (旧挙動) と
/// 100µs / 1ms (新挙動) でスループットを比較する。
///
/// 仕様: 64 concurrent commit で <c>GroupCommitWindow=100µs</c> が default 比 3× 以上。
///
/// 起動方法: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --ft27-groupcommit</c>
/// </summary>
public static class Ft27GroupCommitRunner
{
    public static int Run()
    {
        Console.WriteLine("=== FT-27: WAL Group Commit ===");

        int[] threadCounts = { 1, 8, 32, 64 };
        TimeSpan[] windows =
        {
            TimeSpan.Zero,
            TimeSpan.FromMicroseconds(100),
            TimeSpan.FromMilliseconds(1),
        };

        Console.WriteLine("threads, window_us, commits, elapsed_ms, commits/sec, batches, avg_batch");
        foreach (int n in threadCounts)
        {
            foreach (var w in windows)
            {
                var r = MeasureThroughput(n, w, durationMs: 2_000);
                Console.WriteLine(
                    $"{n},{w.TotalMicroseconds:F0},{r.Commits},{r.ElapsedMs},{r.CommitsPerSec:F0}," +
                    $"{r.Batches},{r.AvgBatch:F1}");
            }
            Console.WriteLine();
        }

        return 0;
    }

    private readonly record struct Result(
        long Commits, long ElapsedMs, double CommitsPerSec, long Batches, double AvgBatch);

    private static Result MeasureThroughput(int threadCount, TimeSpan window, int durationMs)
    {
        string dir = BenchTempDir.Create("ft27_gc");
        Directory.CreateDirectory(dir);
        try
        {
            using var wal = new WriteAheadLog(Path.Combine(dir, "wal"), 256L * 1024 * 1024, window);

            long commits = 0;
            using var stop = new ManualResetEventSlim(false);
            using var start = new ManualResetEventSlim(false);
            var threads = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                int tid = i;
                threads[i] = new Thread(() =>
                {
                    start.Wait();
                    while (!stop.IsSet)
                    {
                        long lsn = wal.Append(WalRecordType.Commit, new TransactionId(tid + 1), []);
                        wal.FlushTo(lsn);
                        Interlocked.Increment(ref commits);
                    }
                }) { IsBackground = true };
                threads[i].Start();
            }

            // wait for threads to spin up
            Thread.Sleep(50);
            long batchesBefore = wal.FlushBatchCount;
            var sw = Stopwatch.StartNew();
            start.Set();
            Thread.Sleep(durationMs);
            stop.Set();
            foreach (var t in threads) t.Join();
            sw.Stop();
            long batches = wal.FlushBatchCount - batchesBefore;

            return new Result(
                commits,
                sw.ElapsedMilliseconds,
                commits / Math.Max(1.0, sw.Elapsed.TotalSeconds),
                batches,
                batches == 0 ? 0 : (double)commits / batches);
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }
}

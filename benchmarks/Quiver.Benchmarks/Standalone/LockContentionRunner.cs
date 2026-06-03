using System.Diagnostics;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// FT-24: shared/exclusive lock の contention 特性を BDN 無しで短時間計測するランナー。
///
/// 二段構成:
///   1) Lock micro-bench — <see cref="LockManager"/> を直接叩き、N threads が同じ entityId を
///      奪い合う throughput を Shared / Exclusive で比較。Shared が増えるほど並列化される
///      ことを示す。仕様の「ReaderWriter が ExclusiveOnly 比 5× 以上」期待値を直接検証する。
///   2) DB-level macro bench — <see cref="GraphDatabase"/> 経由で 32 reader + 1 writer の
///      throughput を <see cref="LockingMode.ExclusiveOnly"/> vs <see cref="LockingMode.ReaderWriter"/>
///      で比較。
///
/// 起動方法: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --ft24-lock</c>
/// </summary>
public static class LockContentionRunner
{
    public static int Run()
    {
        Console.WriteLine("=== FT-24: Lock Contention ===");
        RunLockMicro();
        Console.WriteLine();
        RunDbMacro();
        return 0;
    }

    // ------------------------------------------------------------------------
    // 1) LockManager micro-bench
    // ------------------------------------------------------------------------
    private static void RunLockMicro()
    {
        Console.WriteLine("-- (1) LockManager micro-bench (N threads, same entityId, 1s) --");
        Console.WriteLine("threads, mode, totalAcquires, acquires/sec");

        int[] threadCounts = { 1, 4, 8, 16, 32 };
        int durationMs = 1_000;

        foreach (var n in threadCounts)
        {
            long exTotal = MeasureLockMicro(n, LockMode.Exclusive, durationMs);
            Console.WriteLine($"{n},Exclusive,{exTotal},{exTotal * 1000L / durationMs}");
            long shTotal = MeasureLockMicro(n, LockMode.Shared, durationMs);
            Console.WriteLine($"{n},Shared,{shTotal},{shTotal * 1000L / durationMs}");
            double ratio = exTotal == 0 ? 0 : (double)shTotal / exTotal;
            Console.WriteLine($"  → Shared / Exclusive = {ratio:F2}×");
        }
    }

    private static long MeasureLockMicro(int threadCount, LockMode mode, int durationMs)
    {
        var lm = new LockManager();
        var stop = new ManualResetEventSlim(false);
        var ready = new CountdownEvent(threadCount);
        long[] counts = new long[threadCount];
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int local = t;
            threads[t] = new Thread(() =>
            {
                var tx = new TransactionId(local + 1);
                ready.Signal();
                stop.Wait();
                long c = 0;
                while (!Volatile.Read(ref s_stopFlag))
                {
                    if (lm.TryAcquire(42L, tx, mode, TimeSpan.FromSeconds(1)))
                    {
                        lm.Release(42L, tx);
                        c++;
                    }
                }
                counts[local] = c;
            });
            threads[t].IsBackground = true;
            threads[t].Start();
        }

        ready.Wait();
        Volatile.Write(ref s_stopFlag, false);
        stop.Set();
        Thread.Sleep(durationMs);
        Volatile.Write(ref s_stopFlag, true);
        foreach (var th in threads) th.Join();

        long total = 0;
        for (int i = 0; i < counts.Length; i++) total += counts[i];
        return total;
    }

    private static bool s_stopFlag;

    // ------------------------------------------------------------------------
    // 2) GraphDatabase macro bench
    // ------------------------------------------------------------------------
    private static void RunDbMacro()
    {
        Console.WriteLine("-- (2) GraphDatabase macro-bench (32 readers + 1 writer, 3s) --");
        Console.WriteLine("mode, readerTotal, reader/sec, writerTotal, writer/sec");

        int durationMs = 3_000;
        var ex = MeasureDb(LockingMode.ExclusiveOnly, durationMs);
        Console.WriteLine($"ExclusiveOnly,{ex.Reads},{ex.Reads * 1000L / durationMs},{ex.Writes},{ex.Writes * 1000L / durationMs}");
        var rw = MeasureDb(LockingMode.ReaderWriter, durationMs);
        Console.WriteLine($"ReaderWriter,{rw.Reads},{rw.Reads * 1000L / durationMs},{rw.Writes},{rw.Writes * 1000L / durationMs}");
        double readRatio = ex.Reads == 0 ? 0 : (double)rw.Reads / ex.Reads;
        Console.WriteLine($"  → reader throughput RW / Ex = {readRatio:F2}×");
    }

    private static (long Reads, long Writes) MeasureDb(LockingMode mode, int durationMs)
    {
        var dir = BenchTempDir.Create("ft24");
        try
        {
            // Seed: 1 hot node with a Int64 property.
            NodeId hot;
            using (var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"), new GraphDatabaseOptions
            {
                LockingMode = mode,
                LockTimeout = TimeSpan.FromSeconds(2),
            }))
            {
                using var tx = db.BeginTransaction();
                hot = tx.CreateNode("Hot");
                tx.SetProperty(hot, "v", PropertyValue.FromInt64(0L));
                tx.Commit();
            }

            using var db2 = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"), new GraphDatabaseOptions
            {
                LockingMode = mode,
                LockTimeout = TimeSpan.FromSeconds(2),
            });

            int readerCount = 32;
            long[] readerCounts = new long[readerCount];
            long writerCount = 0;
            var stop = new ManualResetEventSlim(false);
            var ready = new CountdownEvent(readerCount + 1);
            var threads = new Thread[readerCount + 1];

            for (int t = 0; t < readerCount; t++)
            {
                int local = t;
                threads[t] = new Thread(() =>
                {
                    ready.Signal();
                    stop.Wait();
                    long c = 0;
                    while (!Volatile.Read(ref s_stopFlag2))
                    {
                        try
                        {
                            using var rtx = db2.BeginReadOnlyTransaction();
                            _ = rtx.GetProperty(hot, "v").Int64Value;
                            c++;
                        }
                        catch { /* timeout under heavy contention is expected; not counted */ }
                    }
                    readerCounts[local] = c;
                })
                { IsBackground = true };
                threads[t].Start();
            }

            threads[readerCount] = new Thread(() =>
            {
                ready.Signal();
                stop.Wait();
                long c = 0;
                long v = 1;
                while (!Volatile.Read(ref s_stopFlag2))
                {
                    try
                    {
                        using var wtx = db2.BeginTransaction();
                        wtx.SetProperty(hot, "v", PropertyValue.FromInt64(v++));
                        wtx.Commit();
                        c++;
                    }
                    catch { }
                }
                Volatile.Write(ref writerCount, c);
            })
            { IsBackground = true };
            threads[readerCount].Start();

            ready.Wait();
            Volatile.Write(ref s_stopFlag2, false);
            stop.Set();
            Thread.Sleep(durationMs);
            Volatile.Write(ref s_stopFlag2, true);
            foreach (var th in threads) th.Join();

            long reads = 0;
            for (int i = 0; i < readerCounts.Length; i++) reads += readerCounts[i];
            return (reads, Volatile.Read(ref writerCount));
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static bool s_stopFlag2;
}

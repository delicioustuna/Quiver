using System.Collections.Concurrent;
using System.Diagnostics;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// reader なし writer と、32 個の long-lived snapshot reader を伴う writer の
/// commit p50 を同一プロセス・同一 workload で比較する Wave 4 性能ゲート。
/// </summary>
public static class SingleWriterPerfRunner
{
    private const int ReaderCount = 32;
    private const int WarmupCommits = 20;
    private const int MeasuredCommits = 300;

    public static int Run()
    {
        Console.WriteLine("=== Single writer + snapshot readers ===");
        Console.WriteLine($"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
                          $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

        double readerlessP50 = Measure(readerCount: 0, out _);
        double readersP50 = Measure(ReaderCount, out long readerOperations);
        double ratio = readersP50 / readerlessP50;
        bool passed = readerOperations > 0 && ratio <= 1.5;

        Console.WriteLine("readers, writer_commit_p50_us, reader_operations");
        Console.WriteLine($"0, {readerlessP50:F2}, 0");
        Console.WriteLine($"{ReaderCount}, {readersP50:F2}, {readerOperations}");
        Console.WriteLine($"ratio_vs_readerless={ratio:F3}, gate<=1.500: {(passed ? "PASS" : "FAIL")}");
        return passed ? 0 : 1;
    }

    private static double Measure(int readerCount, out long readerOperations)
    {
        string dir = BenchTempDir.Create($"single_writer_{readerCount}");
        try
        {
            using var db = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));
            VertexId hub = Seed(db);
            for (int i = 0; i < WarmupCommits; i++) CommitOne(db);

            if (readerCount == 0)
            {
                readerOperations = 0;
                return MeasureWriter(db);
            }

            using var ready = new CountdownEvent(readerCount);
            using var firstRead = new CountdownEvent(readerCount);
            using var start = new ManualResetEventSlim(false);
            var errors = new ConcurrentQueue<Exception>();
            var operations = new long[readerCount];
            var threads = new Thread[readerCount];
            int stop = 0;

            for (int index = 0; index < readerCount; index++)
            {
                int local = index;
                threads[index] = new Thread(() =>
                {
                    try
                    {
                        using var tx = db.BeginReadOnlyTransaction();
                        ready.Signal();
                        start.Wait();
                        bool first = true;
                        while (Volatile.Read(ref stop) == 0)
                        {
                            int count = 0;
                            var edges = tx.EnumerateEdges(hub, Direction.Outgoing);
                            while (edges.MoveNext()) count++;
                            edges.Dispose();
                            if (count != 64)
                                throw new InvalidOperationException($"Snapshot reader observed {count} edges; expected 64.");
                            operations[local]++;
                            if (first)
                            {
                                first = false;
                                firstRead.Signal();
                            }
                            Thread.Sleep(1);
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Enqueue(exception);
                        if (ready.CurrentCount > 0) ready.Signal();
                        if (firstRead.CurrentCount > 0) firstRead.Signal();
                    }
                })
                {
                    IsBackground = true,
                    Name = $"quiver-snapshot-reader-{local}",
                };
                threads[index].Start();
            }

            ready.Wait();
            start.Set();
            firstRead.Wait();
            double p50;
            try
            {
                p50 = MeasureWriter(db);
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                foreach (Thread thread in threads) thread.Join();
            }

            if (!errors.IsEmpty) throw new AggregateException(errors);
            readerOperations = operations.Sum();
            return p50;
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static VertexId Seed(QuiverDatabase db)
    {
        using var tx = db.BeginTransaction();
        var hub = tx.CreateVertex("Hub");
        _ = tx.CreateVertex("WriterPayload");
        for (int i = 0; i < 64; i++)
        {
            var neighbor = tx.CreateVertex("Neighbor");
            tx.CreateEdge(hub, neighbor, "LINK");
        }
        tx.Commit();
        return hub;
    }

    private static double MeasureWriter(QuiverDatabase db)
    {
        var elapsedTicks = new long[MeasuredCommits];
        for (int i = 0; i < MeasuredCommits; i++)
        {
            long started = Stopwatch.GetTimestamp();
            CommitOne(db);
            elapsedTicks[i] = Stopwatch.GetTimestamp() - started;
        }

        Array.Sort(elapsedTicks);
        return elapsedTicks[elapsedTicks.Length / 2] * 1_000_000.0 / Stopwatch.Frequency;
    }

    private static void CommitOne(QuiverDatabase db)
    {
        using var tx = db.BeginTransaction();
        tx.CreateVertex("WriterPayload");
        tx.Commit();
    }
}

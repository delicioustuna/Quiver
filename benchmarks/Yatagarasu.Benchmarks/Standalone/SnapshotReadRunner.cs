using System.Diagnostics;
using System.Reflection;
using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks.Standalone;

internal static class SnapshotReadRunner
{
    internal static void Run()
    {
        var probe = typeof(Yatagarasu.Storage.PagedFile).Assembly.GetType("Yatagarasu.Storage.PoolProbe")?.GetField("Values", BindingFlags.Static | BindingFlags.NonPublic);
        string path = Path.Combine(Path.GetTempPath(), "snapshot-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var database = YatagarasuDatabase.Open(Path.Combine(path, "graph.yata"));
            var manager = (TransactionManager)database.BackendInternal.Transactions;
            var vertices = new VertexId[512];
            using (var write = database.BeginWriteTransaction())
            {
                for (int i = 0; i < vertices.Length; i++) vertices[i] = write.CreateVertex("item");
                write.Commit();
            }
            Console.WriteLine($"# cores={Environment.ProcessorCount}; framework={Environment.Version}; probe={probe is not null}; ticksPerSecond={Stopwatch.Frequency}");
            Console.WriteLine("workload,threads,run,iterationsPerThread,wallMs,cpuMs,allocatedBytes,opsPerSecond,monitorContentions,acquisitions,contended,acquireTicks,holdTicks,hits,misses,evictions,frameAcquireTicks");
            foreach (string workload in new[] { "empty", "small", "multi-page" })
            foreach (int count in new[] { 1, 2, 4 })
            for (int run = 1; run <= 3; run++)
            {
                int iterations = workload == "multi-page" ? 5000 : 100000;
                using var ready = new CountdownEvent(count);
                using var start = new ManualResetEventSlim();
                var allocated = new long[count];
                var values = new long[count][];
                var failures = new Exception?[count];
                var workers = Enumerable.Range(0, count).Select(worker => new Thread(() =>
                {
                    void Read()
                    {
                        if (workload == "empty") { using var read = manager.BeginRead(); return; }
                        using var transaction = database.BeginReadTransaction();
                        if (workload == "multi-page")
                            for (int i = 0; i < vertices.Length; i += 32)
                                if (!transaction.VertexExists(vertices[i])) throw new InvalidOperationException("Missing vertex.");
                    }
                    try { for (int i = 0; i < 100; i++) Read(); }
                    catch (Exception ex) { failures[worker] = ex; }
                    var stats = (long[]?)probe?.GetValue(null);
                    if (stats is not null) Array.Clear(stats);
                    ready.Signal();
                    start.Wait();
                    if (failures[worker] is not null) return;
                    try
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < iterations; i++) Read();
                        allocated[worker] = GC.GetAllocatedBytesForCurrentThread() - before;
                        values[worker] = stats ?? new long[8];
                    }
                    catch (Exception ex) { failures[worker] = ex; }
                })).ToArray();
                foreach (var worker in workers) worker.Start();
                ready.Wait();
                using var process = Process.GetCurrentProcess();
                TimeSpan cpu = process.TotalProcessorTime;
                long contentions = Monitor.LockContentionCount;
                var wall = Stopwatch.StartNew();
                start.Set();
                foreach (var worker in workers) worker.Join();
                wall.Stop();
                if (failures.Any(ex => ex is not null)) throw new AggregateException(failures.OfType<Exception>());
                long[] total = Enumerable.Range(0, 8).Select(i => values.Sum(v => v[i])).ToArray();
                if (probe is not null && total[4] + total[5] != total[0])
                    throw new InvalidOperationException("Unexpected probe counts.");
                string diagnostics = probe is null ? string.Join(',', Enumerable.Repeat("NA", 8)) : string.Join(',', total);
                Console.WriteLine(FormattableString.Invariant($"{workload},{count},{run},{iterations},{wall.Elapsed.TotalMilliseconds:F3},{(process.TotalProcessorTime - cpu).TotalMilliseconds:F3},{allocated.Sum()},{count * iterations / wall.Elapsed.TotalSeconds:F0},{Monitor.LockContentionCount - contentions},{diagnostics}"));
            }
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}

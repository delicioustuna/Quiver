using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Benchmarks.Standalone;

internal static class BufferPoolReadRunner
{
    internal static void Run()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "buffer-pool-" + Guid.NewGuid().ToString("N") + ".yata");
        var probe = typeof(PagedFile).Assembly.GetType("Yatagarasu.Storage.PoolProbe")?.GetField("Values", BindingFlags.Static | BindingFlags.NonPublic);
        Console.WriteLine($"# cores={Environment.ProcessorCount}; framework={Environment.Version}; probe={probe is not null}; ticksPerSecond={Stopwatch.Frequency}");
        Console.WriteLine("workload,threads,run,iterationsPerThread,wallMs,cpuMs,allocatedBytes,opsPerSecond,monitorContentions,acquisitions,contended,acquireTicks,holdTicks,hits,misses,evictions,frameAcquireTicks");
        try
        {
            using (var setup = new PagedFile(path, poolCapacity: 128))
            {
                for (int i = 1; i <= 128; i++)
                {
                    var id = setup.AllocatePage(PageKind.VertexRecord);
                    using var page = setup.PinForWrite(id);
                    BinaryPrimitives.WriteInt64LittleEndian(page.Data, id.Value);
                }
                setup.Flush();
            }
            foreach (string workload in new[] { "single-shared", "multi-shared", "multi-disjoint", "eviction" })
            foreach (int count in new[] { 1, 2, 4 })
            for (int run = 1; run <= 3; run++)
            {
                using var file = new PagedFile(path, poolCapacity: workload == "eviction" ? 8 : 128);
                int iterations = workload == "eviction" ? 1000 : 100000;
                using var ready = new CountdownEvent(count);
                using var start = new ManualResetEventSlim();
                var allocated = new long[count];
                var values = new long[count][];
                var failures = new Exception?[count];
                var workers = Enumerable.Range(0, count).Select(worker => new Thread(() =>
                {
                    void Read(int iteration)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            long id = workload switch
                            {
                                "single-shared" => 1,
                                "multi-shared" => 1 + i,
                                "multi-disjoint" => 1 + worker * 16 + i,
                                _ => 1 + (iteration * 16 + i + worker * 32) % 128
                            };
                            using var page = file.PinForRead(new PageId(id));
                            if (BinaryPrimitives.ReadInt64LittleEndian(page.Data) != id)
                                throw new InvalidOperationException("Page contents changed.");
                        }
                    }
                    try { for (int i = 0; i < 100; i++) Read(i); }
                    catch (Exception ex) { failures[worker] = ex; }
                    var stats = (long[]?)probe?.GetValue(null);
                    if (stats is not null) Array.Clear(stats);
                    ready.Signal();
                    start.Wait();
                    if (failures[worker] is not null) return;
                    try
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < iterations; i++) Read(i);
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
                if (probe is not null && (total[0] != count * iterations * 16L || total[4] + total[5] != total[0]
                    || (workload != "eviction" && total[5] != 0) || (workload == "eviction" && total[6] == 0)))
                    throw new InvalidOperationException("Unexpected probe counts.");
                string diagnostics = probe is null ? string.Join(',', Enumerable.Repeat("NA", 8)) : string.Join(',', total);
                Console.WriteLine(FormattableString.Invariant($"{workload},{count},{run},{iterations},{wall.Elapsed.TotalMilliseconds:F3},{(process.TotalProcessorTime - cpu).TotalMilliseconds:F3},{allocated.Sum()},{count * iterations / wall.Elapsed.TotalSeconds:F0},{Monitor.LockContentionCount - contentions},{diagnostics}"));
            }
        }
        finally { File.Delete(path); }
    }
}

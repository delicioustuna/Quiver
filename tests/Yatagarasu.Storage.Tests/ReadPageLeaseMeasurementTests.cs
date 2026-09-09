using System.Diagnostics;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Yatagarasu.Storage.Tests;

[CollectionDefinition("resident-read-measurement", DisableParallelization = true)]
public sealed class ResidentReadMeasurementCollection;

[Collection("resident-read-measurement")]
public sealed class ReadPageLeaseMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public void Small_resident_read_workload_has_bounded_allocations()
    {
        const int iterations = 100_000;
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_read_measure_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path);
            var pageId = file.AllocatePage(PageKind.SlottedHeap);
            using (var write = file.PinForWrite(pageId)) write.Data[0] = 7;
            output.WriteLine($"CPU logical cores: {Environment.ProcessorCount}; resident pages: 1; iterations/thread: {iterations}");
            foreach (int count in new[] { 1, 4 })
            for (int run = 1; run <= 3; run++)
            {
                using var ready = new CountdownEvent(count);
                using var start = new ManualResetEventSlim();
                var workers = new Thread[count];
                long[] allocations = new long[count];
                long[] sums = new long[count];
                Exception?[] failures = new Exception?[count];
                for (int index = 0; index < count; index++)
                {
                    int worker = index;
                    workers[index] = new Thread(() =>
                    {
                        try
                        {
                            for (int i = 0; i < 2000; i++) { using var warmup = file.PinForRead(pageId); }
                            ready.Signal();
                            start.Wait();
                            long before = GC.GetAllocatedBytesForCurrentThread();
                            long sum = 0;
                            for (int i = 0; i < iterations; i++)
                            {
                                using var read = file.PinForRead(pageId);
                                sum += read.Data[0];
                            }
                            allocations[worker] = GC.GetAllocatedBytesForCurrentThread() - before;
                            sums[worker] = sum;
                        }
                        catch (Exception exception) { failures[worker] = exception; }
                    }) { IsBackground = true };
                    workers[index].Start();
                }
                try
                {
                    ready.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    using Process process = Process.GetCurrentProcess();
                    TimeSpan cpuBefore = process.TotalProcessorTime;
                    var watch = Stopwatch.StartNew();
                    start.Set();
                    foreach (Thread worker in workers) worker.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
                    watch.Stop();
                    double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                    failures.Should().OnlyContain(exception => exception == null);
                    sums.Sum().Should().Be((long)count * iterations * 7);
                    output.WriteLine($"threads={count} run={run} wall_ms={watch.Elapsed.TotalMilliseconds:F3} cpu_ms={cpuMs:F3} allocated_bytes={allocations.Sum()} ops_s={count * iterations / watch.Elapsed.TotalSeconds:F0}");
#if !DEBUG
                    allocations.Sum().Should().BeLessThan(count * 4096L);
#endif
                }
                finally
                {
                    start.Set();
                    foreach (Thread worker in workers) worker.Join(TimeSpan.FromSeconds(20));
                }
            }
        }
        finally { File.Delete(path); }
    }
}

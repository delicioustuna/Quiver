using System.Reflection;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Storage.Tests;

public sealed class BufferPoolShardTests
{
    [Fact]
    public void Hit_meter_callback_runs_outside_shard_and_failure_returns_pin()
    {
        WithFile(32, (file, pages) =>
        {
            using (var warm = file.PinForRead(pages[0])) { }
            int thread = Environment.CurrentManagedThreadId;
            bool invoked = false;
            bool held = false;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, active) =>
            {
                if (instrument.Name == "yatagarasu.buffer_pool.hits") active.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            {
                if (Environment.CurrentManagedThreadId != thread) return;
                invoked = true;
                held = ShardGate(file, pages[0]).IsHeldByCurrentThread;
                file.Flush();
                throw new InvalidOperationException("Listener failed.");
            });
            listener.Start();
            Action pin = () => { using var page = file.PinForRead(pages[0]); };
            pin.Should().Throw<InvalidOperationException>().WithMessage("Listener failed.");
            listener.Dispose();
            invoked.Should().BeTrue();
            held.Should().BeFalse();
            file.Truncate(1);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Concurrent_readers_and_writer_keep_same_and_different_shard_pages_consistent(bool sameShard)
    {
        WithFile(32, (file, pages) =>
        {
            Lock first = ShardGate(file, pages[0]);
            PageId other = pages.Skip(1).First(page => (first == ShardGate(file, page)) == sameShard);
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 500; i++)
                {
                    var id = i % 2 == 0 ? pages[0] : other;
                    if (worker == 0)
                    {
                        using var write = file.PinForWrite(id);
                        write.Data[1] = (byte)i;
                        write.Data[2] = (byte)i;
                        if (i % 50 == 0) file.Flush();
                    }
                    else
                    {
                        using var read = file.PinForRead(id);
                        read.Data[1].Should().Be(read.Data[2]);
                    }
                }
            })).ToArray();
            start.Set();
            Task.WaitAll(tasks, TimeSpan.FromSeconds(10)).Should().BeTrue();
        });
    }

    [Fact]
    public void Resident_hit_does_not_wait_for_global_coordination()
    {
        WithFile(32, (file, pages) =>
        {
            using (var warm = file.PinForRead(pages[0])) { }
            var global = (Lock)typeof(PagedFile).GetField("_poolLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
            AssertReadCompletesWhileHeld(file, pages[0], global);
        });
    }

    [Fact]
    public void Resident_hit_in_another_shard_does_not_wait_for_a_held_shard()
    {
        WithFile(32, (file, pages) =>
        {
            Lock first = ShardGate(file, pages[0]);
            PageId other = pages.First(page => first != ShardGate(file, page));
            using (var warm = file.PinForRead(other)) { }
            AssertReadCompletesWhileHeld(file, other, first);
        });
    }

    [Fact]
    public async Task Random_reads_survive_eviction_flush_truncate_and_remap()
    {
        string path = Path.Combine(Path.GetTempPath(), "shard-lifecycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path, poolCapacity: 8,
                initialFileAllocationBytes: PagedFile.PageSizeConst * 2L,
                maximumFileGrowthStepBytes: PagedFile.PageSizeConst * 2L);
            var pages = Enumerable.Range(0, 32).Select(_ => file.AllocatePage(PageKind.SlottedHeap)).ToArray();
            foreach (var id in pages) { using var page = file.PinForWrite(id); page.Data[0] = (byte)id.Value; }
            file.Flush();
            using var start = new ManualResetEventSlim();
            var readers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                var random = new Random(7123 + worker);
                start.Wait();
                for (int i = 0; i < 400; i++)
                {
                    var id = pages[random.Next(pages.Length)];
                    using var page = file.PinForRead(id);
                    page.Data[0].Should().Be((byte)id.Value);
                    if (i % 17 == 0) Thread.Yield();
                }
            })).ToArray();
            var maintenance = Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 12; i++)
                {
                    file.AllocatePage(PageKind.SlottedHeap);
                    file.Flush();
                    file.Truncate(33);
                }
            });
            start.Set();
            await Task.WhenAll(readers.Append(maintenance)).WaitAsync(TimeSpan.FromSeconds(20));
            foreach (var id in pages) { using var page = file.PinForRead(id); page.Data[0].Should().Be((byte)id.Value); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Dispose_preserves_existing_read_lease_and_rejects_new_pins()
    {
        WithFile(32, (file, pages) =>
        {
            using var held = file.PinForRead(pages[0]);
            var close = Task.Run(file.Dispose);
            close.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            close.GetAwaiter().GetResult();
            held.Data[0].Should().Be((byte)pages[0].Value);
            Action pin = () => { using var page = file.PinForRead(pages[0]); };
            pin.Should().Throw<ObjectDisposedException>();
        });
    }

    private static Lock ShardGate(PagedFile file, PageId page)
    {
        object shard = typeof(PagedFile).GetMethod("GetShard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(file, [page])!;
        return (Lock)shard.GetType().GetField("Gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shard)!;
    }

    private static void AssertReadCompletesWhileHeld(PagedFile file, PageId page, Lock gate)
    {
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        var reader = new Thread(() =>
        {
            try { using var read = file.PinForRead(page); read.Data[0].Should().Be((byte)page.Value); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        }) { IsBackground = true };
        bool completed;
        lock (gate)
        {
            reader.Start();
            completed = done.Wait(TimeSpan.FromSeconds(3));
        }
        reader.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
        failure.Should().BeNull();
        completed.Should().BeTrue();
    }

    private static void WithFile(int capacity, Action<PagedFile, PageId[]> action)
    {
        string path = Path.Combine(Path.GetTempPath(), "shard-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path, poolCapacity: capacity);
            var pages = Enumerable.Range(0, 32).Select(_ => file.AllocatePage(PageKind.SlottedHeap)).ToArray();
            foreach (var id in pages) { using var page = file.PinForWrite(id); page.Data[0] = (byte)id.Value; }
            file.Flush();
            action(file, pages);
        }
        finally { File.Delete(path); }
    }
}

using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Yatagarasu.Storage.Tests;

public sealed class ReadPageLeaseTests
{
    [Fact]
    public void Double_release_and_stale_frame_copies_do_not_release_a_new_pin()
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_read_copy_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path, poolCapacity: 2);
            var pages = Enumerable.Range(0, 8).Select(_ => file.AllocatePage(PageKind.SlottedHeap)).ToArray();
            var first = file.PinForRead(pages[0]);
            var stale = first;
            first.Dispose();
            Release(ref first).Should().BeOfType<InvalidOperationException>();
            foreach (var pageId in pages.Skip(1)) { using var churn = file.PinForRead(pageId); }
            using var current = file.PinForRead(pages[0]);
            Release(ref stale).Should().BeOfType<InvalidOperationException>();
            current.PageId.Should().Be(pages[0]);
        }
        finally { File.Delete(path); }
    }

#if DEBUG
    [Fact]
    public void Copied_read_lease_cannot_release_another_read_on_the_same_frame()
    {
        using var file = new InMemoryPagedFile();
        var pageId = file.AllocatePage(PageKind.SlottedHeap);
        var first = file.PinForRead(pageId);
        var copy = first;
        first.Dispose();
        using var current = file.PinForRead(pageId);
        Release(ref copy).Should().BeOfType<InvalidOperationException>();
    }
#endif

    [Fact]
    public void Failed_write_lock_acquisition_does_not_leave_a_pin()
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_pin_acquire_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path);
            var pageId = file.AllocatePage(PageKind.SlottedHeap);
            using (var read = file.PinForRead(pageId))
            {
                Action upgrade = () => { using var write = file.PinForWrite(pageId); };
                upgrade.Should().Throw<LockRecursionException>();
            }
            file.Truncate(1);
            file.PageCount.Should().Be(1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Active_pin_survives_concurrent_eviction_and_remapping()
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_read_pressure_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path, poolCapacity: 8,
                initialFileAllocationBytes: 2L * PagedFile.PageSizeConst,
                maximumFileGrowthStepBytes: 2L * PagedFile.PageSizeConst);
            var pages = Enumerable.Range(0, 32).Select(_ => file.AllocatePage(PageKind.SlottedHeap)).ToArray();
            foreach (var pageId in pages)
            {
                using var write = file.PinForWrite(pageId);
                write.Data[0] = checked((byte)pageId.Value);
            }
            using var start = new ManualResetEventSlim();
            using var exercised = new CountdownEvent(4);
            Task[] workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 1000; i++)
                {
                    var pageId = pages[1 + (i + worker * 7) % 31];
                    using var read = file.PinForRead(pageId);
                    read.Data[0].Should().Be((byte)pageId.Value);
                    if (i == 20) exercised.Signal();
                }
            })).ToArray();
            // 同期区間内で pin を保持し、await をまたいで lock owner thread を変えない。
            using (var held = file.PinForRead(pages[0]))
            {
                start.Set();
                exercised.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                for (int i = 0; i < 8; i++) file.AllocatePage(PageKind.SlottedHeap);
                held.Data[0].Should().Be((byte)pages[0].Value);
            }
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            file.Flush();
            file.Truncate(1);
        }
        finally { File.Delete(path); }
    }

    private static Exception? Release(ref PageReadHandle handle)
    {
        try { handle.Dispose(); return null; }
        catch (Exception exception) { return exception; }
    }

    [Fact]
    public void Read_release_does_not_wait_for_the_pool_lock()
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_read_release_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path);
            var pageId = file.AllocatePage(PageKind.SlottedHeap);
            Lock gate = (Lock)typeof(PagedFile).GetField("_poolLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
            using var pinned = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            Exception? failure = null;
            var reader = new Thread(() =>
            {
                try
                {
                    var handle = file.PinForRead(pageId);
                    pinned.Set();
                    release.Wait();
                    handle.Dispose();
                    released.Set();
                }
                catch (Exception exception) { failure = exception; pinned.Set(); }
            }) { IsBackground = true };
            reader.Start();
            bool completed;
            try
            {
                pinned.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                lock (gate)
                {
                    release.Set();
                    completed = released.Wait(TimeSpan.FromSeconds(2));
                }
            }
            finally
            {
                release.Set();
                reader.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            failure.Should().BeNull();
            completed.Should().BeTrue("read release は pool lock を取得しないため");
        }
        finally { File.Delete(path); }
    }
}

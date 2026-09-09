using System.Reflection;
using System.IO.MemoryMappedFiles;
using FluentAssertions;
using Xunit;

namespace Yatagarasu.Storage.Tests;

public sealed class WritePageLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Double_release_and_stale_copies_preserve_the_current_write(bool unchanged)
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_write_copy_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path, poolCapacity: 2);
            var pages = Enumerable.Range(0, 8).Select(_ => file.AllocatePage(PageKind.SlottedHeap)).ToArray();
            var first = file.PinForWrite(pages[0]);
            var stale = first;
            Release(ref first, unchanged).Should().BeNull();
            Release(ref first, !unchanged).Should().BeOfType<InvalidOperationException>();
            foreach (var id in pages.Skip(1)) { using var churn = file.PinForRead(id); }
            using var current = file.PinForWrite(pages[0]);
            Release(ref stale, unchanged).Should().BeOfType<InvalidOperationException>();
            current.Data[0] = 42;
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Transferred_handle_is_the_only_release_owner(bool memory, bool tenant)
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_write_transfer_" + Guid.NewGuid().ToString("N"));
        try
        {
            using IPagedFile physical = memory ? new InMemoryPagedFile() : new PagedFile(path);
            using var container = tenant ? new SingleFileContainer(physical) : null;
            using IPagedFile file = container?.OpenTenant(1, PageKind.SlottedHeap) ?? physical;
            var id = file.AllocatePage(PageKind.SlottedHeap);
            var original = file.PinForWrite(id);
            var owner = original.Transfer();
            Release(ref original, false).Should().BeOfType<InvalidOperationException>();
            Release(ref original, true).Should().BeOfType<InvalidOperationException>();
            owner.Data[0] = 42;
            owner.Lsn = 17;
            owner.Dispose();
            using var current = file.PinForRead(id);
            current.Data[0].Should().Be(42);
            PageHeader.ReadLsn(current.Raw).Should().Be(17);
            PageHeader.Validate(current.Raw, current.PageId);
        }
        finally { File.Delete(path); }
    }

#if DEBUG
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copied_write_lease_cannot_release_a_new_pin_on_the_same_frame(bool unchanged)
    {
        using var file = new InMemoryPagedFile();
        var id = file.AllocatePage(PageKind.SlottedHeap);
        var original = file.PinForWrite(id);
        var copy = original;
        original.Dispose();
        using var current = file.PinForWrite(id);
        Release(ref copy, unchanged).Should().BeOfType<InvalidOperationException>();
        current.Data[0] = 42;
    }
#endif

    private static Exception? Release(ref PageWriteHandle handle, bool unchanged)
    {
        try
        {
            if (unchanged) handle.ReleaseUnchanged(); else handle.Dispose();
            return null;
        }
        catch (Exception exception) { return exception; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_release_does_not_wait_for_the_pool_lock(bool unchanged)
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_write_release_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path);
            var pageId = file.AllocatePage(PageKind.SlottedHeap);
            Lock gate = (Lock)typeof(PagedFile).GetField("_poolLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
            using var pinned = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            Exception? failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    var handle = file.PinForWrite(pageId);
                    if (!unchanged) handle.Data[0] = 42;
                    pinned.Set();
                    release.Wait();
                    if (unchanged) handle.ReleaseUnchanged(); else handle.Dispose();
                    released.Set();
                }
                catch (Exception exception) { failure = exception; pinned.Set(); }
            }) { IsBackground = true };
            writer.Start();
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
                writer.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            failure.Should().BeNull();
            completed.Should().BeTrue("write release は pool lock を取得しないため");
            using var read = file.PinForRead(pageId);
            read.Data[0].Should().Be(unchanged ? (byte)0 : (byte)42);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Active_write_survives_eviction_pressure_and_remapping()
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_write_pressure_" + Guid.NewGuid().ToString("N"));
        try
        {
            Yatagarasu.Core.PageId id;
            using (var file = new PagedFile(path, poolCapacity: 4,
                initialFileAllocationBytes: 2L * PagedFile.PageSizeConst,
                maximumFileGrowthStepBytes: 2L * PagedFile.PageSizeConst))
            {
                id = file.AllocatePage(PageKind.SlottedHeap);
                using (var write = file.PinForWrite(id))
                {
                    write.Data[0] = 42;
                    for (int i = 0; i < 24; i++)
                    {
                        var next = file.AllocatePage(PageKind.SlottedHeap);
                        using var read = file.PinForRead(next);
                        read.Data[0].Should().Be(0);
                    }
                    write.Data[0].Should().Be(42);
                }
                file.Flush();
            }
            using var reopened = new PagedFile(path);
            using var persisted = reopened.PinForRead(id);
            persisted.Data[0].Should().Be(42);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Flush_persists_a_committed_page_while_a_reader_is_pinned()
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_read_flush_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path);
            var id = file.AllocatePage(PageKind.SlottedHeap);
            using (var write = file.PinForWrite(id)) write.Data[0] = 42;
            using var read = file.PinForRead(id);
            file.Flush();
            var disk = (MemoryMappedViewAccessor)typeof(PagedFile).GetField("_viewAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
            disk.ReadByte(id.Value * PagedFile.PageSizeConst + PageHeader.Size).Should().Be(42);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flush_does_not_persist_a_write_pinned_dirty_page(bool otherThread)
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_write_flush_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path);
            var pageId = file.AllocatePage(PageKind.SlottedHeap);
            using (var first = file.PinForWrite(pageId)) first.Data[0] = 7;
            var disk = (MemoryMappedViewAccessor)typeof(PagedFile).GetField("_viewAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
            using (var current = file.PinForWrite(pageId))
            {
                current.Data[0] = 42;
                if (otherThread)
                {
                    Exception? failure = null;
                    var worker = new Thread(() =>
                    {
                        try { file.Flush(); }
                        catch (Exception exception) { failure = exception; }
                    }) { IsBackground = true };
                    worker.Start();
                    worker.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    failure.Should().BeNull();
                }
                else file.Flush();
                disk.ReadByte(pageId.Value * PagedFile.PageSizeConst + PageHeader.Size).Should().Be(0);
            }
            file.Flush();
            disk.ReadByte(pageId.Value * PagedFile.PageSizeConst + PageHeader.Size).Should().Be(42);
        }
        finally { File.Delete(path); }
    }
}

using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Yatagarasu.Storage.Tests;

public sealed class ReadOnlyAnalysisTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Cold_reads_do_not_evict_dirty_pages_and_cached_reads_keep_latest_data(int capacity)
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_analysis_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path, poolCapacity: capacity);
            var pages = Enumerable.Range(0, 20).Select(_ => file.AllocatePage(PageKind.SlottedHeap)).ToArray();
            file.Flush();
            using (var page = file.PinForWrite(pages[^1])) page.Data[0] = 42;
            byte[] before = ReadDisk(file);
            using (file.BeginReadOnlyAnalysis())
            {
                using (file.BeginReadOnlyAnalysis())
                    foreach (var id in pages)
                    {
                        using var page = file.PinForRead(id);
                        page.Data[0].Should().Be(id == pages[^1] ? (byte)42 : (byte)0);
                    }
                foreach (var id in pages.Reverse()) { using var page = file.PinForRead(id); }
                ReadDisk(file).Should().Equal(before);
            }
            file.Flush();
            ReadDisk(file).Should().NotEqual(before);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Detached_read_survives_scope_exit_and_rejects_double_dispose()
    {
        string path = Path.Combine(Path.GetTempPath(), "yatagarasu_detached_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new PagedFile(path, poolCapacity: 2);
            var first = file.AllocatePage(PageKind.SlottedHeap);
            for (int i = 0; i < 10; i++) file.AllocatePage(PageKind.SlottedHeap);
            file.Flush();
            var scope = file.BeginReadOnlyAnalysis();
            var read = file.PinForRead(first);
            scope.Dispose();
            scope.Dispose();
            using (var write = file.PinForWrite(first)) write.Data[0] = 42;
            read.Data[0].Should().Be(0);
            read.Dispose();
            Exception? failure = null;
            try { read.Dispose(); } catch (Exception e) { failure = e; }
            failure.Should().BeOfType<InvalidOperationException>();
        }
        finally { File.Delete(path); }
    }

    private static byte[] ReadDisk(PagedFile file)
    {
        var stream = (FileStream)typeof(PagedFile).GetField("_fileStream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
        byte[] bytes = new byte[checked((int)RandomAccess.GetLength(stream.SafeFileHandle))];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = RandomAccess.Read(stream.SafeFileHandle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return bytes;
    }
}

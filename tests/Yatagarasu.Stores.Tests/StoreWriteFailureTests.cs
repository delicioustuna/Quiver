using System.Buffers.Binary;
using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Yatagarasu.Storage.Wal;
using Xunit;

namespace Yatagarasu.Storage.Records.Tests;

public sealed class StoreWriteFailureTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in new[] { "vertex", "edge", "nexus" })
        foreach (bool shortened in new[] { false, true })
        foreach (string backend in new[] { "disk", "memory", "tenant-disk", "tenant-memory" })
            yield return [kind, shortened, backend];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Invalid_slot_preserves_the_exception_and_releases_the_write_pin(string kind, bool shortened, string backend)
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_write_failure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            IPagedFile OpenPhysical(string name) => backend.EndsWith("memory", StringComparison.Ordinal)
                ? new InMemoryPagedFile()
                : new PagedFile(Path.Combine(directory, name));
            using var container = backend.StartsWith("tenant", StringComparison.Ordinal)
                ? new SingleFileContainer(OpenPhysical("container")) : null;
            using IPagedFile heapFile = container?.OpenTenant(1, PageKind.SlottedHeap) ?? OpenPhysical("heap");
            using IPagedFile mapFile = container?.OpenTenant(2, PageKind.ItemPointerMap) ?? OpenPhysical("map");
            var map = new ItemPointerMap(mapFile);
            var versions = new InMemoryEntityVersionStore();
            var heap = new VersionedRecordHeap(heapFile, map);
            var payload = new byte[45];
            payload[0] = 1;
            ItemPointer pointer = heap.Insert(0, payload, 1);
            versions.Write(0, new EntityVersionMeta(1, 0, 1));
            Action write;
            switch (kind)
            {
                case "vertex":
                    var vertices = new VersionedVertexStore(heapFile, map, null, versions);
                    write = () => { using var handle = vertices.Write(VertexId.Create(0, 1)); };
                    break;
                case "edge":
                    var edges = new VersionedEdgeStore(heapFile, map, versions);
                    write = () => { using var handle = edges.Write(EdgeId.Create(0, 1)); };
                    break;
                default:
                    var nexuses = new VersionedNexusStore(heapFile, map, versions);
                    write = () => { using var handle = nexuses.Write(NexusId.Create(0, 1)); };
                    break;
            }

            var pageId = new PageId(pointer.PageId);
            using (var page = heapFile.PinForWrite(pageId))
            {
                if (shortened)
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        page.Data[(SlottedPage.SlotDirStart + pointer.Slot * SlottedPage.SlotEntrySize + 2)..], 1);
                else
                    new SlottedPage(page.Data).Delete(pointer.Slot).Should().BeTrue();
            }
            byte[] before;
            using (var page = heapFile.PinForRead(pageId)) before = page.Raw.ToArray();
            heapFile.Flush();
            using var wal = new NullWriteAheadLog();
            var writeSet = new WalWriteSet(wal, new TransactionId(2));
            wal.ActiveWriteSet = writeSet;
            if (container is null) heapFile.EnableWalLogging(1, wal);
            else container.EnableWalLogging(1, wal);

            if (shortened) write.Should().Throw<ArgumentOutOfRangeException>();
            else write.Should().Throw<CorruptionException>();
            writeSet.FlushPending();
            wal.CurrentLsn.Should().Be(0, "検証失敗では after-image を登録しないため");

            // 同じ thread の再入で漏れた write lock を隠さない。
            Task probe = Task.Run(() =>
            {
                using (var page = heapFile.PinForRead(pageId))
                    page.Raw.ToArray().Should().Equal(before);
                using var nextWrite = heapFile.PinForWrite(pageId);
            });
            await probe.WaitAsync(TimeSpan.FromSeconds(5));
            writeSet.FlushPending();
            wal.CurrentLsn.Should().Be(1, "通常の write dispose は after-image を登録するため");
            wal.ActiveWriteSet = null;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

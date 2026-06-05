using System.IO;
using System.Text;
using FluentAssertions;
using Quiver.Storage;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// ARCH-5c Phase 1: ItemPointerMap + VersionedRecordHeap 骨格の単体テスト。
/// 可視性は SI を模した述語を注入して検証する (MvccContext 非依存)。
/// </summary>
public class VersionedRecordHeapTests : IDisposable
{
    private readonly string _mapPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _heapPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private PagedFile _mapFile;
    private PagedFile _heapFile;
    private ItemPointerMap _map;
    private VersionedRecordHeap _heap;

    public VersionedRecordHeapTests()
    {
        _mapFile = new PagedFile(_mapPath);
        _heapFile = new PagedFile(_heapPath);
        _map = new ItemPointerMap(_mapFile);
        _heap = new VersionedRecordHeap(_heapFile, _map);
    }

    public void Dispose()
    {
        _mapFile.Dispose();
        _heapFile.Dispose();
        File.Delete(_mapPath);
        File.Delete(_heapPath);
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>SI を模した可視性: snapshot 以下の xmin が見え、xmax 0 または snapshot 超で生存。</summary>
    private static VersionVisible SnapshotAt(long s)
        => (xmin, xmax) => xmin <= s && (xmax == 0 || xmax > s);

    [Fact]
    public void Insert_and_read_visible()
    {
        _heap.Insert(seq: 0, Bytes("v1"), xmin: 1);
        _heap.TryReadVisible(0, SnapshotAt(1), out var p).Should().BeTrue();
        p.Should().Equal(Bytes("v1"));
    }

    [Fact]
    public void Unknown_seq_is_not_visible()
    {
        _heap.TryReadVisible(99, SnapshotAt(100), out var p).Should().BeFalse();
        p.Should().BeEmpty();
    }

    [Fact]
    public void AppendVersion_keeps_old_version_for_old_snapshot()
    {
        _heap.Insert(seq: 0, Bytes("v1"), xmin: 1);
        _heap.AppendVersion(seq: 0, Bytes("v2"), xmin: 2);

        // snapshot=1: 新 head (xmin=2) は不可視 → チェーンを辿り v1 (xmax=2>1 で生存)
        _heap.TryReadVisible(0, SnapshotAt(1), out var older).Should().BeTrue();
        older.Should().Equal(Bytes("v1"));

        // snapshot=2: head v2 (xmin=2, xmax=0) が可視
        _heap.TryReadVisible(0, SnapshotAt(2), out var newer).Should().BeTrue();
        newer.Should().Equal(Bytes("v2"));
    }

    [Fact]
    public void StampXmax_hides_after_deletion_snapshot()
    {
        _heap.Insert(seq: 0, Bytes("v1"), xmin: 1);
        _heap.StampXmax(seq: 0, xmax: 3);

        // snapshot=2: 削除 (xmax=3) はまだ未来 → 生存
        _heap.TryReadVisible(0, SnapshotAt(2), out _).Should().BeTrue();
        // snapshot=3: 削除確定 → 不可視
        _heap.TryReadVisible(0, SnapshotAt(3), out _).Should().BeFalse();
    }

    [Fact]
    public void Many_entries_span_multiple_pages()
    {
        const int n = 500;
        var payload = new byte[256];
        for (int i = 0; i < n; i++)
        {
            payload[0] = (byte)(i & 0xFF);
            payload[1] = (byte)((i >> 8) & 0xFF);
            _heap.Insert(i, payload, xmin: 1);
        }
        for (int i = 0; i < n; i++)
        {
            _heap.TryReadVisible(i, SnapshotAt(1), out var p).Should().BeTrue();
            p.Length.Should().Be(256);
            p[0].Should().Be((byte)(i & 0xFF));
            p[1].Should().Be((byte)((i >> 8) & 0xFF));
        }
    }

    [Fact]
    public void Map_persists_across_reopen()
    {
        _map.Set(5, new ItemPointer(7, 3));
        _map.Hwm.Should().Be(6);
        _mapFile.Dispose();

        _mapFile = new PagedFile(_mapPath);
        var reopened = new ItemPointerMap(_mapFile);
        reopened.Hwm.Should().Be(6);
        var ptr = reopened.Get(5);
        ptr.PageId.Should().Be(7);
        ptr.Slot.Should().Be(3);
        reopened.Get(4).IsNull.Should().BeTrue(); // 未設定は null
    }

    [Fact]
    public void Heap_and_map_persist_across_reopen()
    {
        _heap.Insert(seq: 0, Bytes("durable"), xmin: 1);
        _heapFile.Dispose();
        _mapFile.Dispose();

        _mapFile = new PagedFile(_mapPath);
        _heapFile = new PagedFile(_heapPath);
        _map = new ItemPointerMap(_mapFile);
        _heap = new VersionedRecordHeap(_heapFile, _map);

        _heap.TryReadVisible(0, SnapshotAt(1), out var p).Should().BeTrue();
        p.Should().Equal(Bytes("durable"));
    }
}

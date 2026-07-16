using System.IO;
using System.Linq;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// VersionedVertexStore (heap+map+sidecar 上の IVertexStore drop-in) の単体テスト。
/// MVCC コンテキスト無し (= Bootstrap / committed registry null) で実行する。
/// MVCC / generation は永続 EntityVersionStore sidecar に載せ、reopen でも保持する。
/// </summary>
public class VersionedVertexStoreTests : IDisposable
{
    private readonly string _heapPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _mapPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _verPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private PagedFile _heapFile;
    private PagedFile _mapFile;
    private PagedFile _verFile;
    private ItemPointerMap _map;
    private EntityVersionStore _versions;
    private VersionedVertexStore _store;

    public VersionedVertexStoreTests()
    {
        _heapFile = new PagedFile(_heapPath);
        _mapFile = new PagedFile(_mapPath);
        _verFile = new PagedFile(_verPath);
        _map = new ItemPointerMap(_mapFile);
        _versions = new EntityVersionStore(_verFile);
        _store = new VersionedVertexStore(_heapFile, _map, labelIndex: null, _versions);
    }

    public void Dispose()
    {
        _heapFile.Dispose();
        _mapFile.Dispose();
        _verFile.Dispose();
        File.Delete(_heapPath);
        File.Delete(_mapPath);
        File.Delete(_verPath);
    }

    private void Reopen()
    {
        _heapFile.Dispose(); _mapFile.Dispose(); _verFile.Dispose();
        _heapFile = new PagedFile(_heapPath);
        _mapFile = new PagedFile(_mapPath);
        _verFile = new PagedFile(_verPath);
        _map = new ItemPointerMap(_mapFile);
        _versions = new EntityVersionStore(_verFile);
        _store = new VersionedVertexStore(_heapFile, _map, labelIndex: null, _versions);
    }

    [Fact]
    public void Allocate_returns_valid_vertex()
    {
        var id = _store.Allocate(new LabelId(1));
        id.IsValid.Should().BeTrue();
        _store.InUseCount.Should().Be(1);
        id.Generation.Should().Be(1);
    }

    [Fact]
    public void Read_returns_allocated_label_and_invalid_pointers()
    {
        var id = _store.Allocate(new LabelId(42));
        using var h = _store.Read(id);
        h.InUse.Should().BeTrue();
        h.Label.Value.Should().Be(42);
        h.FirstEdgeId.IsValid.Should().BeFalse();
        h.FirstPropertyRef.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Write_persists_pointers_in_place()
    {
        var id = _store.Allocate(new LabelId(1));
        {
            var w = _store.Write(id);
            w.FirstEdgeId = new EdgeId(7);
            w.FirstPropertyRef = new PropertyVersionRef(9);
            w.Dispose();
        }
        using var r = _store.Read(id);
        r.FirstEdgeId.Value.Should().Be(7);
        r.FirstPropertyRef.Value.Should().Be(9);
        r.Label.Value.Should().Be(1);
    }

    [Fact]
    public void Free_logically_deletes_and_hides_from_read()
    {
        var id = _store.Allocate(new LabelId(1));
        _store.Free(id);
        _store.InUseCount.Should().Be(0);
        using var r = _store.Read(id);
        r.InUse.Should().BeFalse();
    }

    [Fact]
    public void Scan_returns_only_live_vertices()
    {
        var a = _store.Allocate(new LabelId(1));
        var b = _store.Allocate(new LabelId(1));
        var c = _store.Allocate(new LabelId(1));
        _store.Free(b);

        var live = _store.Scan().Select(n => n.Sequence).OrderBy(x => x).ToList();
        live.Should().Equal(a.Sequence, c.Sequence);
    }

    [Fact]
    public void Sequences_are_monotonic_no_reuse()
    {
        var a = _store.Allocate(new LabelId(1));
        var b = _store.Allocate(new LabelId(1));
        _store.Free(a);
        var c = _store.Allocate(new LabelId(1));
        c.Sequence.Should().BeGreaterThan(b.Sequence);
    }

    [Fact]
    public void CurrentGeneration_returns_one_for_live_and_minus_one_out_of_range()
    {
        var id = _store.Allocate(new LabelId(1));
        _store.CurrentGeneration(id.Sequence).Should().Be(1);
        _store.CurrentGeneration(999).Should().Be(-1);
        _store.CurrentGeneration(-5).Should().Be(-1);
    }

    [Fact]
    public void Stale_generation_handle_reads_as_not_in_use()
    {
        var id = _store.Allocate(new LabelId(1));
        var stale = VertexId.Create(id.Sequence, generation: 2);
        using var r = _store.Read(stale);
        r.InUse.Should().BeFalse();
    }

    [Fact]
    public void Many_vertices_span_multiple_heap_pages()
    {
        const int n = 1000;
        var ids = new VertexId[n];
        for (int i = 0; i < n; i++) ids[i] = _store.Allocate(new LabelId((short)(i % 7)));
        _store.InUseCount.Should().Be(n);
        for (int i = 0; i < n; i++)
        {
            using var r = _store.Read(ids[i]);
            r.InUse.Should().BeTrue();
            r.Label.Value.Should().Be((short)(i % 7));
        }
    }

    [Fact]
    public void State_persists_across_reopen()
    {
        var id = _store.Allocate(new LabelId(5));
        {
            var w = _store.Write(id);
            w.FirstPropertyRef = new PropertyVersionRef(11);
            w.Dispose();
        }
        var freed = _store.Allocate(new LabelId(6));
        _store.Free(freed);

        Reopen();

        _store.InUseCount.Should().Be(1);
        using var r = _store.Read(id);
        r.InUse.Should().BeTrue();
        r.Label.Value.Should().Be(5);
        r.FirstPropertyRef.Value.Should().Be(11);
        using var rf = _store.Read(freed);
        rf.InUse.Should().BeFalse();
    }

    [Fact]
    public void Vacuum_removes_dead_vertices_below_horizon()
    {
        var a = _store.Allocate(new LabelId(1));
        var b = _store.Allocate(new LabelId(1));
        _store.Free(b); // xmax = Bootstrap

        // Bootstrap は CommittedTxRegistry に常に登録済。horizon を十分大きく取る。
        var committed = new CommittedTxRegistry();
        int reclaimed = _store.VacuumDeadVersions(horizonTxId: long.MaxValue, committed);
        reclaimed.Should().Be(1);

        // a は生存、b は heap から消えて Scan に出ない
        var live = _store.Scan().Select(n => n.Sequence).ToList();
        live.Should().Equal(a.Sequence);
    }

}

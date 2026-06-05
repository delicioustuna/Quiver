using System.IO;
using System.Linq;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// ARCH-5c Phase 2: VersionedNodeStore (heap+map+sidecar 上の INodeStore drop-in) の単体テスト。
/// MVCC コンテキスト無し (= Bootstrap / committed registry null) で実行する。
/// MVCC / generation は永続 EntityVersionStore sidecar に載せ、reopen でも保持する。
/// </summary>
public class VersionedNodeStoreTests : IDisposable
{
    private readonly string _heapPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _mapPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _verPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private PagedFile _heapFile;
    private PagedFile _mapFile;
    private PagedFile _verFile;
    private ItemPointerMap _map;
    private EntityVersionStore _versions;
    private VersionedNodeStore _store;

    public VersionedNodeStoreTests()
    {
        _heapFile = new PagedFile(_heapPath);
        _mapFile = new PagedFile(_mapPath);
        _verFile = new PagedFile(_verPath);
        _map = new ItemPointerMap(_mapFile);
        _versions = new EntityVersionStore(_verFile);
        _store = new VersionedNodeStore(_heapFile, _map, labelIndex: null, _versions);
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
        _store = new VersionedNodeStore(_heapFile, _map, labelIndex: null, _versions);
    }

    [Fact]
    public void Allocate_returns_valid_node()
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
        h.FirstRelationshipId.IsValid.Should().BeFalse();
        h.FirstPropertyId.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Write_persists_pointers_in_place()
    {
        var id = _store.Allocate(new LabelId(1));
        {
            var w = _store.Write(id);
            w.FirstRelationshipId = new RelationshipId(7);
            w.FirstPropertyId = new PropertyId(9);
            w.Dispose();
        }
        using var r = _store.Read(id);
        r.FirstRelationshipId.Value.Should().Be(7);
        r.FirstPropertyId.Value.Should().Be(9);
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
    public void Scan_returns_only_live_nodes()
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
        var stale = NodeId.Create(id.Sequence, generation: 2);
        using var r = _store.Read(stale);
        r.InUse.Should().BeFalse();
    }

    [Fact]
    public void Many_nodes_span_multiple_heap_pages()
    {
        const int n = 1000;
        var ids = new NodeId[n];
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
            w.FirstPropertyId = new PropertyId(11);
            w.Dispose();
        }
        var freed = _store.Allocate(new LabelId(6));
        _store.Free(freed);

        Reopen();

        _store.InUseCount.Should().Be(1);
        using var r = _store.Read(id);
        r.InUse.Should().BeTrue();
        r.Label.Value.Should().Be(5);
        r.FirstPropertyId.Value.Should().Be(11);
        using var rf = _store.Read(freed);
        rf.InUse.Should().BeFalse();
    }

    [Fact]
    public void Vacuum_removes_dead_nodes_below_horizon()
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

    // ===== ARCH-5c Phase 3: inline property =====

    [Fact]
    public void Inline_property_scalar_roundtrip()
    {
        var id = _store.Allocate(new LabelId(1));
        _store.SetInlineProperty(id, new PropertyKeyId(10), PropertyValue.FromInt32(42)).Should().BeTrue();
        _store.HasInlineProperty(id, new PropertyKeyId(10)).Should().BeTrue();
        _store.TryGetInlineProperty(id, new PropertyKeyId(10), out var v).Should().BeTrue();
        v.Int32Value.Should().Be(42);
        _store.HasInlineProperty(id, new PropertyKeyId(99)).Should().BeFalse();
    }

    [Fact]
    public void Inline_property_string_roundtrip()
    {
        var id = _store.Allocate(new LabelId(1));
        _store.SetInlineProperty(id, new PropertyKeyId(7), PropertyValue.FromString("hello")).Should().BeTrue();
        _store.TryGetInlineProperty(id, new PropertyKeyId(7), out var v).Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(v.Utf8StringValue).Should().Be("hello");
    }

    [Fact]
    public void Inline_property_replace_updates_value()
    {
        var id = _store.Allocate(new LabelId(1));
        var key = new PropertyKeyId(5);
        _store.SetInlineProperty(id, key, PropertyValue.FromInt32(1));
        _store.SetInlineProperty(id, key, PropertyValue.FromInt32(2));
        _store.TryGetInlineProperty(id, key, out var v).Should().BeTrue();
        v.Int32Value.Should().Be(2);
    }

    [Fact]
    public void Inline_property_remove()
    {
        var id = _store.Allocate(new LabelId(1));
        var key = new PropertyKeyId(5);
        _store.SetInlineProperty(id, key, PropertyValue.FromBool(true));
        _store.RemoveInlineProperty(id, key).Should().BeTrue();
        _store.HasInlineProperty(id, key).Should().BeFalse();
        _store.TryGetInlineProperty(id, key, out _).Should().BeFalse();
        _store.RemoveInlineProperty(id, key).Should().BeFalse(); // 二度目は無し
    }

    [Fact]
    public void Inline_multiple_keys_independent()
    {
        var id = _store.Allocate(new LabelId(1));
        _store.SetInlineProperty(id, new PropertyKeyId(1), PropertyValue.FromInt32(100));
        _store.SetInlineProperty(id, new PropertyKeyId(2), PropertyValue.FromString("x"));
        _store.SetInlineProperty(id, new PropertyKeyId(3), PropertyValue.FromBool(true));

        _store.TryGetInlineProperty(id, new PropertyKeyId(1), out var v1).Should().BeTrue();
        v1.Int32Value.Should().Be(100);
        _store.TryGetInlineProperty(id, new PropertyKeyId(2), out var v2).Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(v2.Utf8StringValue).Should().Be("x");
        _store.TryGetInlineProperty(id, new PropertyKeyId(3), out var v3).Should().BeTrue();
        v3.BoolValue.Should().BeTrue();

        // 1 つ消しても他は残る
        _store.RemoveInlineProperty(id, new PropertyKeyId(2));
        _store.HasInlineProperty(id, new PropertyKeyId(1)).Should().BeTrue();
        _store.HasInlineProperty(id, new PropertyKeyId(2)).Should().BeFalse();
        _store.HasInlineProperty(id, new PropertyKeyId(3)).Should().BeTrue();
    }

    [Fact]
    public void Inline_rejects_oversized_value()
    {
        var id = _store.Allocate(new LabelId(1));
        var big = new string('a', 300); // > 255 → inline 不可
        _store.SetInlineProperty(id, new PropertyKeyId(1), PropertyValue.FromString(big)).Should().BeFalse();
        _store.HasInlineProperty(id, new PropertyKeyId(1)).Should().BeFalse();
    }

    [Fact]
    public void Inline_property_persists_across_reopen()
    {
        var id = _store.Allocate(new LabelId(1));
        _store.SetInlineProperty(id, new PropertyKeyId(10), PropertyValue.FromInt64(123456789L));
        _store.SetInlineProperty(id, new PropertyKeyId(11), PropertyValue.FromString("persist"));

        Reopen();

        _store.TryGetInlineProperty(id, new PropertyKeyId(10), out var v10).Should().BeTrue();
        v10.Int64Value.Should().Be(123456789L);
        _store.TryGetInlineProperty(id, new PropertyKeyId(11), out var v11).Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(v11.Utf8StringValue).Should().Be("persist");
    }
}

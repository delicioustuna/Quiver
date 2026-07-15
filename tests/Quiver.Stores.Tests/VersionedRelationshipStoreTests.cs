using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// VersionedRelationshipStore (heap+map+sidecar 上の IRelationshipStore drop-in)
/// の単体テスト。MVCC コンテキスト無し (Bootstrap / committed registry) で実行する。ノード側も
/// VersionedNodeStore を組で使い、heap fast-path (GetFirstRelId / UpdateFirstRelId) を通す。
/// </summary>
public class VersionedRelationshipStoreTests : IDisposable
{
    private readonly string _nHeap = Tmp(), _nMap = Tmp(), _nVer = Tmp();
    private readonly string _rHeap = Tmp(), _rMap = Tmp(), _rVer = Tmp();
    private PagedFile _nHeapF = null!, _nMapF = null!, _nVerF = null!, _rHeapF = null!, _rMapF = null!, _rVerF = null!;
    private VersionedNodeStore _nodes = null!;
    private VersionedRelationshipStore _rels = null!;

    private static string Tmp() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public VersionedRelationshipStoreTests() => Open();

    private void Open()
    {
        _nHeapF = new PagedFile(_nHeap); _nMapF = new PagedFile(_nMap); _nVerF = new PagedFile(_nVer);
        _rHeapF = new PagedFile(_rHeap); _rMapF = new PagedFile(_rMap); _rVerF = new PagedFile(_rVer);
        _nodes = new VersionedNodeStore(_nHeapF, new ItemPointerMap(_nMapF), labelIndex: null, new EntityVersionStore(_nVerF));
        _rels = new VersionedRelationshipStore(_rHeapF, new ItemPointerMap(_rMapF), new EntityVersionStore(_rVerF));
    }

    private void Reopen()
    {
        _nHeapF.Dispose(); _nMapF.Dispose(); _nVerF.Dispose();
        _rHeapF.Dispose(); _rMapF.Dispose(); _rVerF.Dispose();
        Open();
    }

    public void Dispose()
    {
        _nHeapF.Dispose(); _nMapF.Dispose(); _nVerF.Dispose();
        _rHeapF.Dispose(); _rMapF.Dispose(); _rVerF.Dispose();
        foreach (var p in new[] { _nHeap, _nMap, _nVer, _rHeap, _rMap, _rVer }) File.Delete(p);
    }

    [Fact]
    public void Create_returns_current_generation_relationship_id()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        rel.IsValid.Should().BeTrue();
        rel.Generation.Should().Be(1);
        rel.Value.Should().NotBe(rel.Sequence);
        _rels.CurrentGeneration(rel.Sequence).Should().Be(1);
        _rels.InUseCount.Should().Be(1);

        Reopen();

        _rels.CurrentGeneration(rel.Sequence).Should().Be(1);
        using var reopened = _rels.Read(rel);
        reopened.InUse.Should().BeTrue();
        reopened.Id.Should().Be(rel);
    }

    [Fact]
    public void Read_returns_correct_endpoints()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(5));
        using var h = _rels.Read(rel);
        h.InUse.Should().BeTrue();
        h.Source.Sequence.Should().Be(a.Sequence);
        h.Target.Sequence.Should().Be(b.Sequence);
        h.Type.Value.Should().Be(5);
    }

    [Fact]
    public void Adjacency_list_bidirectional_integrity()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        using var h = _rels.Read(rel);
        h.SourcePrev.IsValid.Should().BeFalse();
        h.SourceNext.IsValid.Should().BeFalse();
        h.TargetPrev.IsValid.Should().BeFalse();
        h.TargetNext.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Enumerate_returns_all_neighbors()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var c = _nodes.Allocate(new LabelId(1));
        _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        _rels.Create(_nodes, a, c, new RelationshipTypeId(0));

        var ids = new List<RelationshipId>();
        var en = _rels.EnumerateNeighbors(a, _nodes);
        while (en.MoveNext()) ids.Add(en.Current.Id);
        ids.Count.Should().Be(2);
    }

    [Fact]
    public void Enumerate_filters_by_type_and_direction()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var c = _nodes.Allocate(new LabelId(1));
        _rels.Create(_nodes, a, b, new RelationshipTypeId(1)); // a -> b (out)
        _rels.Create(_nodes, c, a, new RelationshipTypeId(1)); // c -> a (in)

        int outCount = 0;
        var en = _rels.EnumerateNeighbors(a, _nodes, new RelationshipTypeId(1), Direction.Outgoing);
        while (en.MoveNext()) outCount++;
        outCount.Should().Be(1);
    }

    [Fact]
    public void Delete_logically_hides_relationship()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var c = _nodes.Allocate(new LabelId(1));
        _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        var r2 = _rels.Create(_nodes, a, c, new RelationshipTypeId(0));
        _rels.Delete(_nodes, r2);
        _rels.InUseCount.Should().Be(1);
        using var h = _rels.Read(r2);
        h.InUse.Should().BeFalse();
    }

    [Fact]
    public void Scan_returns_only_live_relationships()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var c = _nodes.Allocate(new LabelId(1));
        var r1 = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        var r2 = _rels.Create(_nodes, a, c, new RelationshipTypeId(0));
        _rels.Delete(_nodes, r2);

        var live = _rels.Scan().Select(r => r.Sequence).OrderBy(x => x).ToList();
        live.Should().Equal(r1.Sequence);
    }

    [Fact]
    public void Write_updates_firstProp_in_place()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        {
            var w = _rels.Write(rel);
            w.FirstPropertyId = new PropertyId(42);
            w.Dispose();
        }
        using var h = _rels.Read(rel);
        h.FirstPropertyId.Value.Should().Be(42);
        h.Source.Sequence.Should().Be(a.Sequence);
        h.Target.Sequence.Should().Be(b.Sequence);
    }

    [Fact]
    public void Vacuum_does_not_release_relationship_sequence_for_reuse()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var r1 = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        _rels.Delete(_nodes, r1);
        var committed = new CommittedTxRegistry();
        _rels.VacuumDeadVersions(_nodes, long.MaxValue, committed);
        // raw adjacency / delta / locator / epoch entry の lifecycle が完了するまでは
        // relationship sequence を free list へ戻さない。
        _rels.FreeHead.Should().Be(-1);
        var r2 = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        r2.Sequence.Should().BeGreaterThan(r1.Sequence);
        _rels.Read(r1).InUse.Should().BeFalse();
    }

    [Fact]
    public void Many_relationships_span_multiple_heap_pages()
    {
        const int n = 500;
        var hub = _nodes.Allocate(new LabelId(1));
        var rels = new RelationshipId[n];
        for (int i = 0; i < n; i++)
        {
            var leaf = _nodes.Allocate(new LabelId(2));
            rels[i] = _rels.Create(_nodes, hub, leaf, new RelationshipTypeId(0));
        }
        _rels.InUseCount.Should().Be(n);

        int seen = 0;
        var en = _rels.EnumerateNeighbors(hub, _nodes);
        while (en.MoveNext()) seen++;
        seen.Should().Be(n);
    }

    [Fact]
    public void State_persists_across_reopen()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(7));
        {
            var w = _rels.Write(rel);
            w.FirstPropertyId = new PropertyId(3);
            w.Dispose();
        }
        var dead = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        _rels.Delete(_nodes, dead);

        Reopen();

        _rels.InUseCount.Should().Be(1);
        using var h = _rels.Read(rel);
        h.InUse.Should().BeTrue();
        h.Type.Value.Should().Be(7);
        h.FirstPropertyId.Value.Should().Be(3);
        using var hd = _rels.Read(dead);
        hd.InUse.Should().BeFalse();
    }

    // ===== インラインプロパティ =====

    private RelationshipId NewRel()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        return _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
    }

    [Fact]
    public void Inline_property_scalar_roundtrip()
    {
        var rel = NewRel();
        _rels.SetInlineProperty(rel, new PropertyKeyId(10), PropertyValue.FromInt32(42)).Should().BeTrue();
        _rels.HasInlineProperty(rel, new PropertyKeyId(10)).Should().BeTrue();
        _rels.TryGetInlineProperty(rel, new PropertyKeyId(10), out var v).Should().BeTrue();
        v.Int32Value.Should().Be(42);
        _rels.HasInlineProperty(rel, new PropertyKeyId(99)).Should().BeFalse();
    }

    [Fact]
    public void Inline_property_replace_and_remove()
    {
        var rel = NewRel();
        var key = new PropertyKeyId(5);
        _rels.SetInlineProperty(rel, key, PropertyValue.FromInt32(1));
        _rels.SetInlineProperty(rel, key, PropertyValue.FromInt32(2));
        _rels.TryGetInlineProperty(rel, key, out var v).Should().BeTrue();
        v.Int32Value.Should().Be(2);
        _rels.RemoveInlineProperty(rel, key).Should().BeTrue();
        _rels.HasInlineProperty(rel, key).Should().BeFalse();
        _rels.RemoveInlineProperty(rel, key).Should().BeFalse();
    }

    [Fact]
    public void Inline_property_does_not_corrupt_endpoints_or_chain()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var c = _nodes.Allocate(new LabelId(1));
        var r1 = _rels.Create(_nodes, a, b, new RelationshipTypeId(3));
        var r2 = _rels.Create(_nodes, a, c, new RelationshipTypeId(3));
        // r1 に inline property を付けても endpoint / chain は保持される。
        _rels.SetInlineProperty(r1, new PropertyKeyId(1), PropertyValue.FromString("weight"));
        using (var h = _rels.Read(r1))
        {
            h.Source.Sequence.Should().Be(a.Sequence);
            h.Target.Sequence.Should().Be(b.Sequence);
            h.Type.Value.Should().Be(3);
        }
        var neighbors = new List<long>();
        var en = _rels.EnumerateNeighbors(a, _nodes);
        while (en.MoveNext()) neighbors.Add(en.Current.Id.Sequence);
        neighbors.Should().BeEquivalentTo(new[] { r1.Sequence, r2.Sequence });
    }

    [Fact]
    public void Inline_rejects_oversized_value()
    {
        var rel = NewRel();
        var big = new string('a', 300); // > 255 → inline 不可
        _rels.SetInlineProperty(rel, new PropertyKeyId(1), PropertyValue.FromString(big)).Should().BeFalse();
        _rels.HasInlineProperty(rel, new PropertyKeyId(1)).Should().BeFalse();
    }

    [Fact]
    public void Inline_property_persists_across_reopen()
    {
        var rel = NewRel();
        _rels.SetInlineProperty(rel, new PropertyKeyId(10), PropertyValue.FromInt64(123456789L));
        _rels.SetInlineProperty(rel, new PropertyKeyId(11), PropertyValue.FromString("persist"));

        Reopen();

        _rels.TryGetInlineProperty(rel, new PropertyKeyId(10), out var v10).Should().BeTrue();
        v10.Int64Value.Should().Be(123456789L);
        _rels.TryGetInlineProperty(rel, new PropertyKeyId(11), out var v11).Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(v11.Utf8StringValue).Should().Be("persist");
    }

    [Fact]
    public void Vacuum_frees_heap_pages_for_reuse_under_churn()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var committed = new CommittedTxRegistry();
        const int N = 2000;

        // round 1: 多数の rel を inline property 付きで作成 (heap ページを埋める)。
        var rels = new List<RelationshipId>();
        for (int i = 0; i < N; i++)
        {
            var r = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
            _rels.SetInlineProperty(r, new PropertyKeyId(1), PropertyValue.FromInt64(i));
            rels.Add(r);
        }
        long pagesRound1 = _rels.UnderlyingFile.PageCount;

        // 全削除 → vacuum で物理回収 (空ページは free list へ)。
        foreach (var r in rels) _rels.Delete(_nodes, r);
        _rels.VacuumDeadVersions(_nodes, long.MaxValue, committed);

        // round 2: 再び多数作成 → free list のページを再利用し、ファイルはほぼ成長しないはず。
        for (int i = 0; i < N; i++)
        {
            var r = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
            _rels.SetInlineProperty(r, new PropertyKeyId(1), PropertyValue.FromInt64(i));
        }
        long pagesRound2 = _rels.UnderlyingFile.PageCount;

        // 回収が効いていれば round2 は round1 とほぼ同じ (倍化しない)。
        pagesRound2.Should().BeLessThan(pagesRound1 + 5);
    }

    [Fact]
    public void Vacuum_reclaims_dead_relationships_and_rebuilds_chain()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var c = _nodes.Allocate(new LabelId(1));
        var r1 = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        var r2 = _rels.Create(_nodes, a, c, new RelationshipTypeId(0)); // dead
        _rels.Delete(_nodes, r2);

        var committed = new CommittedTxRegistry();
        int reclaimed = _rels.VacuumDeadVersions(_nodes, horizonTxId: long.MaxValue, committed);
        reclaimed.Should().Be(1);

        // r1 のみ残り、a の chain も r1 だけ
        var live = _rels.Scan().Select(r => r.Sequence).ToList();
        live.Should().Equal(r1.Sequence);

        var neighbors = new List<long>();
        var en = _rels.EnumerateNeighbors(a, _nodes);
        while (en.MoveNext()) neighbors.Add(en.Current.Id.Sequence);
        neighbors.Should().Equal(r1.Sequence);
    }
}

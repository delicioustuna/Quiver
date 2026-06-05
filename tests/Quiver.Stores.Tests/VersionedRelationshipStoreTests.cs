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
/// ARCH-5c Phase 4: VersionedRelationshipStore (heap+map+sidecar 上の IRelationshipStore drop-in)
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
    public void Create_returns_valid_rel_in_sequence_space()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        rel.IsValid.Should().BeTrue();
        // 旧 RelationshipStore と同じく rel は Sequence 空間 (gen=0)。
        rel.Generation.Should().Be(0);
        rel.Value.Should().Be(rel.Sequence);
        _rels.InUseCount.Should().Be(1);
    }

    [Fact]
    public void Read_returns_correct_endpoints()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(5));
        using var h = _rels.Read(rel);
        h.InUse.Should().BeTrue();
        h.Source.Should().Be(a);
        h.Target.Should().Be(b);
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
        h.Source.Should().Be(a);
        h.Target.Should().Be(b);
    }

    [Fact]
    public void Reused_sequence_after_vacuum_is_handed_out_again()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var r1 = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        _rels.Delete(_nodes, r1);
        var committed = new CommittedTxRegistry();
        _rels.VacuumDeadVersions(_nodes, long.MaxValue, committed);
        // 回収後 seq は free list に戻り、次の Create で再利用される。
        var r2 = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        r2.Sequence.Should().Be(r1.Sequence);
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Storage.Records.Tests;

/// <summary>
/// VersionedEdgeStore (heap+map+sidecar 上の IEdgeStore drop-in)
/// の単体テスト。MVCC コンテキスト無し (Bootstrap / committed registry) で実行する。Vertex側も
/// VersionedVertexStore を組で使い、heap fast-path (GetFirstEdgeId / UpdateFirstEdgeId) を通す。
/// </summary>
public class VersionedEdgeStoreTests : IDisposable
{
    private readonly string _nHeap = Tmp(), _nMap = Tmp(), _nVer = Tmp();
    private readonly string _rHeap = Tmp(), _rMap = Tmp(), _rVer = Tmp();
    private PagedFile _nHeapF = null!, _nMapF = null!, _nVerF = null!, _rHeapF = null!, _rMapF = null!, _rVerF = null!;
    private VersionedVertexStore _vertices = null!;
    private VersionedEdgeStore _edges = null!;

    private static string Tmp() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public VersionedEdgeStoreTests() => Open();

    private void Open()
    {
        _nHeapF = new PagedFile(_nHeap); _nMapF = new PagedFile(_nMap); _nVerF = new PagedFile(_nVer);
        _rHeapF = new PagedFile(_rHeap); _rMapF = new PagedFile(_rMap); _rVerF = new PagedFile(_rVer);
        _vertices = new VersionedVertexStore(_nHeapF, new ItemPointerMap(_nMapF), labelIndex: null, new EntityVersionStore(_nVerF));
        _edges = new VersionedEdgeStore(_rHeapF, new ItemPointerMap(_rMapF), new EntityVersionStore(_rVerF));
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
    public void Create_returns_current_generation_edge_id()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var edge = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        edge.IsValid.Should().BeTrue();
        edge.Generation.Should().Be(1);
        edge.Value.Should().NotBe(edge.Sequence);
        _edges.CurrentGeneration(edge.Sequence).Should().Be(1);
        _edges.InUseCount.Should().Be(1);

        Reopen();

        _edges.CurrentGeneration(edge.Sequence).Should().Be(1);
        using var reopened = _edges.Read(edge);
        reopened.InUse.Should().BeTrue();
        reopened.Id.Should().Be(edge);
    }

    [Fact]
    public void Read_returns_correct_endpoints()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var edge = _edges.Create(_vertices, a, b, new EdgeTypeId(5));
        using var h = _edges.Read(edge);
        h.InUse.Should().BeTrue();
        h.Source.Sequence.Should().Be(a.Sequence);
        h.Target.Sequence.Should().Be(b.Sequence);
        h.Type.Value.Should().Be(5);
    }

    [Fact]
    public void Adjacency_list_bidirectional_integrity()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var edge = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        using var h = _edges.Read(edge);
        h.SourcePrev.IsValid.Should().BeFalse();
        h.SourceNext.IsValid.Should().BeFalse();
        h.TargetPrev.IsValid.Should().BeFalse();
        h.TargetNext.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Enumerate_returns_all_neighbors()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var c = _vertices.Allocate(new LabelId(1));
        _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        _edges.Create(_vertices, a, c, new EdgeTypeId(0));

        var ids = new List<EdgeId>();
        var en = _edges.EnumerateNeighbors(a, _vertices);
        while (en.MoveNext()) ids.Add(en.Current.Id);
        ids.Count.Should().Be(2);
    }

    [Fact]
    public void Enumerate_filters_by_type_and_direction()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var c = _vertices.Allocate(new LabelId(1));
        _edges.Create(_vertices, a, b, new EdgeTypeId(1)); // a -> b (out)
        _edges.Create(_vertices, c, a, new EdgeTypeId(1)); // c -> a (in)

        int outCount = 0;
        var en = _edges.EnumerateNeighbors(a, _vertices, new EdgeTypeId(1), Direction.Outgoing);
        while (en.MoveNext()) outCount++;
        outCount.Should().Be(1);
    }

    [Fact]
    public void Delete_logically_hides_edge()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var c = _vertices.Allocate(new LabelId(1));
        _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        var r2 = _edges.Create(_vertices, a, c, new EdgeTypeId(0));
        _edges.Delete(_vertices, r2);
        _edges.InUseCount.Should().Be(1);
        using var h = _edges.Read(r2);
        h.InUse.Should().BeFalse();
    }

    [Fact]
    public void Scan_returns_only_live_edges()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var c = _vertices.Allocate(new LabelId(1));
        var r1 = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        var r2 = _edges.Create(_vertices, a, c, new EdgeTypeId(0));
        _edges.Delete(_vertices, r2);

        var live = _edges.Scan().Select(r => r.Sequence).OrderBy(x => x).ToList();
        live.Should().Equal(r1.Sequence);
    }

    [Fact]
    public void Write_updates_first_property_ref_in_place()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var edge = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        {
            var w = _edges.Write(edge);
            w.FirstPropertyRef = new PropertyVersionRef(42);
            w.Dispose();
        }
        using var h = _edges.Read(edge);
        h.FirstPropertyRef.Value.Should().Be(42);
        h.Source.Sequence.Should().Be(a.Sequence);
        h.Target.Sequence.Should().Be(b.Sequence);
    }

    [Fact]
    public void Vacuum_does_not_release_edge_sequence_for_reuse()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var r1 = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        _edges.Delete(_vertices, r1);
        var committed = new CommittedTxRegistry();
        _edges.VacuumDeadVersions(_vertices, long.MaxValue, committed);
        // raw adjacency / delta / locator / epoch entry の lifecycle が完了するまでは
        // edge sequence を free list へ戻さない。
        _edges.FreeHead.Should().Be(-1);
        var r2 = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        r2.Sequence.Should().BeGreaterThan(r1.Sequence);
        _edges.Read(r1).InUse.Should().BeFalse();
    }

    [Fact]
    public void Many_edges_span_multiple_heap_pages()
    {
        const int n = 500;
        var hub = _vertices.Allocate(new LabelId(1));
        var edges = new EdgeId[n];
        for (int i = 0; i < n; i++)
        {
            var leaf = _vertices.Allocate(new LabelId(2));
            edges[i] = _edges.Create(_vertices, hub, leaf, new EdgeTypeId(0));
        }
        _edges.InUseCount.Should().Be(n);

        int seen = 0;
        var en = _edges.EnumerateNeighbors(hub, _vertices);
        while (en.MoveNext()) seen++;
        seen.Should().Be(n);
    }

    [Fact]
    public void State_persists_across_reopen()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var edge = _edges.Create(_vertices, a, b, new EdgeTypeId(7));
        {
            var w = _edges.Write(edge);
            w.FirstPropertyRef = new PropertyVersionRef(3);
            w.Dispose();
        }
        var dead = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        _edges.Delete(_vertices, dead);

        Reopen();

        _edges.InUseCount.Should().Be(1);
        using var h = _edges.Read(edge);
        h.InUse.Should().BeTrue();
        h.Type.Value.Should().Be(7);
        h.FirstPropertyRef.Value.Should().Be(3);
        using var hd = _edges.Read(dead);
        hd.InUse.Should().BeFalse();
    }

    [Fact]
    public void Vacuum_frees_heap_pages_for_reuse_under_churn()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var committed = new CommittedTxRegistry();
        const int N = 2000;

        // round 1: 多数の edge を作成して heap ページを埋める。
        var edges = new List<EdgeId>();
        for (int i = 0; i < N; i++)
        {
            var r = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
            edges.Add(r);
        }
        long pagesRound1 = _edges.UnderlyingFile.PageCount;

        // 全削除 → vacuum で物理回収 (空ページは free list へ)。
        foreach (var r in edges) _edges.Delete(_vertices, r);
        _edges.VacuumDeadVersions(_vertices, long.MaxValue, committed);

        // round 2: 再び多数作成 → free list のページを再利用し、ファイルはほぼ成長しないはず。
        for (int i = 0; i < N; i++)
        {
            _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        }
        long pagesRound2 = _edges.UnderlyingFile.PageCount;

        // 回収が効いていれば round2 は round1 とほぼ同じ (倍化しない)。
        pagesRound2.Should().BeLessThan(pagesRound1 + 5);
    }

    [Fact]
    public void Vacuum_reclaims_dead_edges_and_rebuilds_chain()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var c = _vertices.Allocate(new LabelId(1));
        var r1 = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        var r2 = _edges.Create(_vertices, a, c, new EdgeTypeId(0)); // dead
        _edges.Delete(_vertices, r2);

        var committed = new CommittedTxRegistry();
        int reclaimed = _edges.VacuumDeadVersions(_vertices, horizonTxId: long.MaxValue, committed);
        reclaimed.Should().Be(1);

        // r1 のみ残り、a の chain も r1 だけ
        var live = _edges.Scan().Select(r => r.Sequence).ToList();
        live.Should().Equal(r1.Sequence);

        var neighbors = new List<long>();
        var en = _edges.EnumerateNeighbors(a, _vertices);
        while (en.MoveNext()) neighbors.Add(en.Current.Id.Sequence);
        neighbors.Should().Equal(r1.Sequence);
    }
}

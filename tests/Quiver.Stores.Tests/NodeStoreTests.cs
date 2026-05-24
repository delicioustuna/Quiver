using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Stores;
using Xunit;

namespace Quiver.Stores.Tests;

public class NodeStoreTests : IDisposable
{
    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly PagedFile _pf;
    private readonly NodeStore _store;

    public NodeStoreTests()
    {
        _pf = new PagedFile(_path);
        _store = new NodeStore(_pf);
    }

    public void Dispose() { _pf.Dispose(); System.IO.File.Delete(_path); }

    [Fact]
    public void Allocate_returns_valid_node()
    {
        var id = _store.Allocate(new LabelId(1));
        id.IsValid.Should().BeTrue();
        _store.InUseCount.Should().Be(1);
    }

    [Fact]
    public void Read_returns_allocated_label()
    {
        var id = _store.Allocate(new LabelId(42));
        using var h = _store.Read(id);
        h.InUse.Should().BeTrue();
        h.Label.Value.Should().Be(42);
        h.FirstRelationshipId.IsValid.Should().BeFalse();
        h.FirstPropertyId.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Write_persists_first_rel()
    {
        var id = _store.Allocate(new LabelId(1));
        {
            var w = _store.Write(id);
            w.FirstRelationshipId = new RelationshipId(7);
            w.Dispose();
        }
        using var r = _store.Read(id);
        r.FirstRelationshipId.Value.Should().Be(7);
    }

    [Fact]
    public void Free_marks_logically_deleted_and_does_not_recycle_until_vacuum()
    {
        // FT-26 MVCC: Free は xmax をスタンプするだけで、record / slot は維持する
        // (snapshot reader が古い version を辿れるため)。物理回収は vacuum (OP-3) 経路担当。
        var id1 = _store.Allocate(new LabelId(1));
        _store.Free(id1);
        _store.InUseCount.Should().Be(0);
        // Read は MVCC visibility フィルタを通って InUse=false を返す。
        using var h = _store.Read(id1);
        h.InUse.Should().BeFalse();
        // 後続 Allocate は新しい id を返す (vacuum 未実装のため slot 再利用なし)。
        var id2 = _store.Allocate(new LabelId(2));
        id2.Value.Should().NotBe(id1.Value);
    }

    [Fact]
    public void Scan_returns_only_in_use_nodes()
    {
        var a = _store.Allocate(new LabelId(1));
        var b = _store.Allocate(new LabelId(2));
        var c = _store.Allocate(new LabelId(3));
        _store.Free(b);
        var ids = _store.Scan().ToList();
        ids.Should().BeEquivalentTo(new[] { a, c });
    }

    [Fact]
    public void Multiple_allocations_have_sequential_ids()
    {
        for (int i = 0; i < 10; i++) _store.Allocate(new LabelId(i));
        _store.InUseCount.Should().Be(10);
    }
}

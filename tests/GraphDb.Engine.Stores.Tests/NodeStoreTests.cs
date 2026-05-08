using FluentAssertions;
using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;
using GraphDb.Engine.Stores;
using Xunit;

namespace GraphDb.Engine.Stores.Tests;

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
    public void Free_recycles_id()
    {
        var id1 = _store.Allocate(new LabelId(1));
        _store.Free(id1);
        _store.InUseCount.Should().Be(0);
        var id2 = _store.Allocate(new LabelId(2));
        id2.Value.Should().Be(id1.Value);
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

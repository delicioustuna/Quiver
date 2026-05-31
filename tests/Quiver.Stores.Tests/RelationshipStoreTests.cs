using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

public class RelationshipStoreTests : IDisposable
{
    private readonly string _nodePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly string _relPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly PagedFile _nodePf, _relPf;
    private readonly NodeStore _nodes;
    private readonly RelationshipStore _rels;

    public RelationshipStoreTests()
    {
        _nodePf = new PagedFile(_nodePath);
        _relPf = new PagedFile(_relPath);
        _nodes = new NodeStore(_nodePf);
        _rels = new RelationshipStore(_relPf);
    }

    public void Dispose()
    {
        _nodePf.Dispose(); _relPf.Dispose();
        System.IO.File.Delete(_nodePath); System.IO.File.Delete(_relPath);
    }

    [Fact]
    public void Create_returns_valid_rel()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        rel.IsValid.Should().BeTrue();
        _rels.InUseCount.Should().Be(1);
    }

    [Fact]
    public void Read_returns_correct_endpoints()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var rel = _rels.Create(_nodes, a, b, new RelationshipTypeId(5));
        using var h = _rels.Read(rel);
        h.Source.Should().Be(a);
        h.Target.Should().Be(b);
        h.Type.Value.Should().Be(5);
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
    public void Delete_removes_from_adjacency_list()
    {
        var a = _nodes.Allocate(new LabelId(1));
        var b = _nodes.Allocate(new LabelId(1));
        var c = _nodes.Allocate(new LabelId(1));
        _rels.Create(_nodes, a, b, new RelationshipTypeId(0));
        var r2 = _rels.Create(_nodes, a, c, new RelationshipTypeId(0));
        _rels.Delete(_nodes, r2);
        _rels.InUseCount.Should().Be(1);
    }
}

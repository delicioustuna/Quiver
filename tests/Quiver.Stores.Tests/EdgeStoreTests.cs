using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

public class EdgeStoreTests : IDisposable
{
    private readonly string _vertexPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly string _edgePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly PagedFile _vertexPf, _edgePf;
    private readonly VertexStore _vertices;
    private readonly EdgeStore _edges;

    public EdgeStoreTests()
    {
        _vertexPf = new PagedFile(_vertexPath);
        _edgePf = new PagedFile(_edgePath);
        _vertices = new VertexStore(_vertexPf);
        _edges = new EdgeStore(_edgePf);
    }

    public void Dispose()
    {
        _vertexPf.Dispose(); _edgePf.Dispose();
        System.IO.File.Delete(_vertexPath); System.IO.File.Delete(_edgePath);
    }

    [Fact]
    public void Create_returns_valid_edge()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var edge = _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        edge.IsValid.Should().BeTrue();
        _edges.InUseCount.Should().Be(1);
    }

    [Fact]
    public void Read_returns_correct_endpoints()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var edge = _edges.Create(_vertices, a, b, new EdgeTypeId(5));
        using var h = _edges.Read(edge);
        h.Id.Should().Be(edge);
        h.Source.Sequence.Should().Be(a.Sequence);
        h.Target.Sequence.Should().Be(b.Sequence);
        h.Type.Value.Should().Be(5);
    }

    [Fact]
    public void Read_materializes_current_generation_and_rejects_stale_identity()
    {
        var source = _vertices.Allocate(new LabelId(1));
        var target = _vertices.Allocate(new LabelId(1));
        var edge = _edges.Create(_vertices, source, target, new EdgeTypeId(5));

        edge.Generation.Should().Be(1);
        using (var physicalRead = _edges.Read(new EdgeId(edge.Sequence)))
        {
            physicalRead.InUse.Should().BeTrue();
            physicalRead.Id.Should().Be(edge);
        }

        using var staleRead = _edges.Read(EdgeId.Create(edge.Sequence, generation: 2));
        staleRead.InUse.Should().BeFalse();
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
    public void Delete_removes_from_adjacency_list()
    {
        var a = _vertices.Allocate(new LabelId(1));
        var b = _vertices.Allocate(new LabelId(1));
        var c = _vertices.Allocate(new LabelId(1));
        _edges.Create(_vertices, a, b, new EdgeTypeId(0));
        var r2 = _edges.Create(_vertices, a, c, new EdgeTypeId(0));
        _edges.Delete(_vertices, r2);
        _edges.InUseCount.Should().Be(1);
    }
}

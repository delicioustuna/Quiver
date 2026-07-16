using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class VectorSearchOptionsTests : IDisposable
{
    private const string IndexName = "options-index";
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "quiver_vp2_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Defaults_preserve_previous_hnsw_search_widths()
    {
        var options = new VectorSearchOptions();

        options.EfSearch.Should().Be(200);
        options.FilteredOversampleFactor.Should().Be(8);
        HnswIndex.ComputeSearchEf(k: 10, options, filtered: false).Should().Be(200);
        HnswIndex.ComputeSearchEf(k: 10, options, filtered: true).Should().Be(200);
    }

    [Fact]
    public void Custom_options_control_unfiltered_and_filtered_search_widths()
    {
        var options = new VectorSearchOptions
        {
            EfSearch = 32,
            FilteredOversampleFactor = 6,
        };

        HnswIndex.ComputeSearchEf(k: 10, options, filtered: false).Should().Be(32);
        HnswIndex.ComputeSearchEf(k: 10, options, filtered: true).Should().Be(60);
    }

    [Fact]
    public void InMemory_search_routes_validate_options()
    {
        var store = new InMemoryVectorStore();
        store.CreateVectorIndex(CreateSpec(IndexName));
        store.SetVector(EntityKind.Vertex, 1, IndexName, [1f, 0f, 0f, 0f]);
        var invalid = new VectorSearchOptions { EfSearch = 0 };
        var candidates = new EntityCandidateSet(EntityKind.Vertex, [1]);

        Action single = () => store.KnnSearch(IndexName, [1f, 0f, 0f, 0f], 1, invalid);
        Action filtered = () => store.KnnSearchFiltered(
            IndexName, [1f, 0f, 0f, 0f], 1, candidates, invalid);
        Action batch = () => store.KnnSearchBatch(
            IndexName, new ReadOnlyMemory<float>[] { new float[] { 1f, 0f, 0f, 0f } }, 1, invalid);

        single.Should().Throw<VectorException>().WithMessage("*EfSearch*");
        filtered.Should().Throw<VectorException>().WithMessage("*EfSearch*");
        batch.Should().Throw<VectorException>().WithMessage("*EfSearch*");
    }

    [Fact]
    public void Persistent_and_traversal_routes_forward_options()
    {
        using var db = QuiverDatabase.Open(Path.Combine(_directory, "graph.quiver"));
        db.Vectors.CreateVectorIndex(CreateSpec(IndexName));
        using (var tx = db.BeginTransaction())
        {
            var vertex = tx.CreateVertex("Doc");
            db.Vectors.SetVector(EntityKind.Vertex, vertex.Value, IndexName, [1f, 0f, 0f, 0f]);
            tx.Commit();
        }

        var invalid = new VectorSearchOptions { FilteredOversampleFactor = 0 };
        Action single = () => db.Vectors.KnnSearch(
            IndexName, [1f, 0f, 0f, 0f], 1, invalid);
        Action batch = () => db.Vectors.KnnSearchBatch(
            IndexName, new ReadOnlyMemory<float>[] { new float[] { 1f, 0f, 0f, 0f } }, 1, invalid);

        using var read = db.BeginReadOnlyTransaction();
        var g = read.G(db.Schema);
        Action traversal = () => g.Knn(
            IndexName, [1f, 0f, 0f, 0f], 1, invalid).ToList();
        Action filteredTraversal = () => g.Vertices()
            .FilterByKnn(IndexName, [1f, 0f, 0f, 0f], 1, invalid)
            .ToList();

        single.Should().Throw<VectorException>().WithMessage("*FilteredOversampleFactor*");
        batch.Should().Throw<VectorException>().WithMessage("*FilteredOversampleFactor*");
        traversal.Should().Throw<VectorException>().WithMessage("*FilteredOversampleFactor*");
        filteredTraversal.Should().Throw<VectorException>().WithMessage("*FilteredOversampleFactor*");
    }

    private static VectorIndexSpec CreateSpec(string name) => new(
        name,
        EntityKind.Vertex,
        new PropertyKeyId(1),
        4,
        DistanceMetric.Cosine,
        "test");
}

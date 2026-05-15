using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// VEC-2 persistence contract for <see cref="JsonFileVectorCatalog"/>. Each
/// test starts in a fresh tempdir, mutates the catalog through one instance,
/// then opens a second instance pointed at the same file to assert that the
/// state survived disposal — that's the real "restart" guarantee for the
/// binary backend (codex_advice_3.md §6.5).
/// </summary>
public sealed class JsonFileVectorCatalogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public JsonFileVectorCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_veccat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "vector_catalog.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static VectorIndexSpec MakeSpec(
        string name = "doc-embed",
        int dim = 8,
        DistanceMetric metric = DistanceMetric.Cosine,
        string provider = "test")
        => new(name, EntityKind.Node, new PropertyKeyId(1), dim, metric, provider, NormalizationProfile: null);

    [Fact]
    public void Empty_path_loads_with_no_indexes()
    {
        var catalog = new JsonFileVectorCatalog(_path);
        catalog.ListIndexes().Should().BeEmpty();
        catalog.ListTasks().Should().BeEmpty();
        // File is only written on first mutation.
        File.Exists(_path).Should().BeFalse();
    }

    [Fact]
    public void Created_index_persists_across_reopen()
    {
        var spec = MakeSpec();
        var first = new JsonFileVectorCatalog(_path);
        first.CreateIndex(spec);
        File.Exists(_path).Should().BeTrue();

        var second = new JsonFileVectorCatalog(_path);
        second.TryGetIndex(spec.Name, out var loaded).Should().BeTrue();
        loaded.Should().BeEquivalentTo(spec);
        second.ListIndexes().Should().ContainSingle().Which.Should().BeEquivalentTo(spec);
    }

    [Fact]
    public void Drop_index_is_visible_after_reopen()
    {
        var spec = MakeSpec();
        var first = new JsonFileVectorCatalog(_path);
        first.CreateIndex(spec);
        first.DropIndex(spec.Name).Should().BeTrue();

        var second = new JsonFileVectorCatalog(_path);
        second.TryGetIndex(spec.Name, out _).Should().BeFalse();
        second.ListIndexes().Should().BeEmpty();
    }

    [Fact]
    public void Drop_index_returns_false_when_missing()
    {
        var catalog = new JsonFileVectorCatalog(_path);
        catalog.DropIndex("ghost").Should().BeFalse();
    }

    [Fact]
    public void CreateIndex_duplicate_throws()
    {
        var catalog = new JsonFileVectorCatalog(_path);
        catalog.CreateIndex(MakeSpec());
        var act = () => catalog.CreateIndex(MakeSpec());
        act.Should().Throw<VectorException>().WithMessage("*already exists*");
    }

    [Fact]
    public void Upserted_task_persists_across_reopen()
    {
        var record = new EmbeddingTaskRecord(
            EntityKind.Node, EntityId: 42, IndexName: "doc-embed", ProviderId: "prov",
            State: EmbeddingTaskState.Completed,
            ContentHash: "hash-1",
            LastError: null,
            LastUpdatedAtUtc: DateTimeOffset.Parse("2026-05-15T00:00:00Z"));

        var first = new JsonFileVectorCatalog(_path);
        first.CreateIndex(MakeSpec());
        first.UpsertTask(record);

        var second = new JsonFileVectorCatalog(_path);
        var loaded = second.GetTask(record.Key);
        loaded.Should().BeEquivalentTo(record);
        second.ListTasks().Should().ContainSingle().Which.Should().BeEquivalentTo(record);
    }

    [Fact]
    public void Upsert_overwrites_existing_task_state()
    {
        var key = new EmbeddingTaskKey(EntityKind.Node, 1, "doc-embed", "prov");
        var initial = new EmbeddingTaskRecord(
            key.EntityKind, key.EntityId, key.IndexName, key.ProviderId,
            EmbeddingTaskState.InProgress, ContentHash: null, LastError: null,
            LastUpdatedAtUtc: DateTimeOffset.Parse("2026-05-14T00:00:00Z"));

        var catalog = new JsonFileVectorCatalog(_path);
        catalog.CreateIndex(MakeSpec());
        catalog.UpsertTask(initial);
        catalog.UpsertTask(initial with
        {
            State = EmbeddingTaskState.Completed,
            ContentHash = "h",
            LastUpdatedAtUtc = DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
        });

        var current = catalog.GetTask(key);
        current!.State.Should().Be(EmbeddingTaskState.Completed);
        current.ContentHash.Should().Be("h");
        catalog.ListTasks().Should().ContainSingle();
    }

    [Fact]
    public void Dropping_index_removes_dependent_tasks()
    {
        var catalog = new JsonFileVectorCatalog(_path);
        catalog.CreateIndex(MakeSpec("a"));
        catalog.CreateIndex(MakeSpec("b"));
        catalog.UpsertTask(new EmbeddingTaskRecord(
            EntityKind.Node, 1, "a", "prov",
            EmbeddingTaskState.Completed, null, null, DateTimeOffset.UtcNow));
        catalog.UpsertTask(new EmbeddingTaskRecord(
            EntityKind.Node, 2, "b", "prov",
            EmbeddingTaskState.Completed, null, null, DateTimeOffset.UtcNow));

        catalog.DropIndex("a");

        catalog.ListTasks().Should().ContainSingle()
            .Which.IndexName.Should().Be("b");

        // Survives reopen.
        var reopened = new JsonFileVectorCatalog(_path);
        reopened.ListTasks().Should().ContainSingle()
            .Which.IndexName.Should().Be("b");
    }

    [Fact]
    public void Corrupt_file_throws_on_open()
    {
        File.WriteAllText(_path, "{ not valid json");
        var act = () => new JsonFileVectorCatalog(_path);
        act.Should().Throw<VectorException>().WithMessage("*corrupt*");
    }

    [Fact]
    public void ListTasks_filters_by_index_name()
    {
        var catalog = new JsonFileVectorCatalog(_path);
        catalog.CreateIndex(MakeSpec("a"));
        catalog.CreateIndex(MakeSpec("b"));
        catalog.UpsertTask(new EmbeddingTaskRecord(
            EntityKind.Node, 1, "a", "prov",
            EmbeddingTaskState.Completed, null, null, DateTimeOffset.UtcNow));
        catalog.UpsertTask(new EmbeddingTaskRecord(
            EntityKind.Node, 2, "b", "prov",
            EmbeddingTaskState.Completed, null, null, DateTimeOffset.UtcNow));

        catalog.ListTasks("a").Should().ContainSingle().Which.EntityId.Should().Be(1);
        catalog.ListTasks("b").Should().ContainSingle().Which.EntityId.Should().Be(2);
        catalog.ListTasks().Should().HaveCount(2);
    }
}

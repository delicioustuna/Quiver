using FluentAssertions;
using Microsoft.Data.Sqlite;
using Quiver.Core;
using Quiver.Storage.Sqlite;
using Xunit;

namespace Quiver.Storage.Sqlite.Tests;

/// <summary>
/// VEC-2 persistence contract for <see cref="SqliteVectorCatalog"/>. Symmetric to
/// <c>JsonFileVectorCatalogTests</c>: every test mutates through one
/// catalog, closes its connection, reopens against the same file, and asserts
/// the state survived — that's the durability guarantee VEC-2 promises for the
/// SQLite-backed catalog.
/// </summary>
public sealed class SqliteVectorCatalogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SqliteVectorCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_veccat_sql_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "vector_catalog.sqlite");
    }

    public void Dispose()
    {
        // SQLite holds the file open until pooled connections are reclaimed.
        // We disabled pooling per-connection but clear pools defensively.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* leave the temp dir if Windows is still latching the file */ }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        SqliteSchema.Bootstrap(c);
        return c;
    }

    private static VectorIndexSpec MakeSpec(
        string name = "doc-embed",
        int dim = 8,
        DistanceMetric metric = DistanceMetric.Cosine,
        string provider = "test")
        => new(name, EntityKind.Node, new PropertyKeyId(1), dim, metric, provider, NormalizationProfile: null);

    [Fact]
    public void Empty_database_starts_with_no_indexes()
    {
        using var conn = OpenConnection();
        var catalog = new SqliteVectorCatalog(conn);
        catalog.ListIndexes().Should().BeEmpty();
        catalog.ListTasks().Should().BeEmpty();
    }

    [Fact]
    public void Created_index_persists_across_reconnect()
    {
        var spec = MakeSpec();
        using (var conn = OpenConnection())
        {
            var catalog = new SqliteVectorCatalog(conn);
            catalog.CreateIndex(spec);
        }
        using (var conn = OpenConnection())
        {
            var catalog = new SqliteVectorCatalog(conn);
            catalog.TryGetIndex(spec.Name, out var loaded).Should().BeTrue();
            loaded.Should().BeEquivalentTo(spec);
            catalog.ListIndexes().Should().ContainSingle().Which.Should().BeEquivalentTo(spec);
        }
    }

    [Fact]
    public void Drop_index_is_visible_after_reconnect()
    {
        var spec = MakeSpec();
        using (var conn = OpenConnection())
        {
            var catalog = new SqliteVectorCatalog(conn);
            catalog.CreateIndex(spec);
            catalog.DropIndex(spec.Name).Should().BeTrue();
        }
        using (var conn = OpenConnection())
        {
            var catalog = new SqliteVectorCatalog(conn);
            catalog.TryGetIndex(spec.Name, out _).Should().BeFalse();
            catalog.ListIndexes().Should().BeEmpty();
        }
    }

    [Fact]
    public void Drop_index_returns_false_when_missing()
    {
        using var conn = OpenConnection();
        var catalog = new SqliteVectorCatalog(conn);
        catalog.DropIndex("ghost").Should().BeFalse();
    }

    [Fact]
    public void CreateIndex_duplicate_throws()
    {
        using var conn = OpenConnection();
        var catalog = new SqliteVectorCatalog(conn);
        catalog.CreateIndex(MakeSpec());

        var act = () => catalog.CreateIndex(MakeSpec());
        act.Should().Throw<VectorException>().WithMessage("*already exists*");
    }

    [Fact]
    public void Upserted_task_persists_across_reconnect()
    {
        var record = new EmbeddingTaskRecord(
            EntityKind.Node, EntityId: 42, IndexName: "doc-embed", ProviderId: "prov",
            State: EmbeddingTaskState.Completed,
            ContentHash: "hash-1",
            LastError: null,
            LastUpdatedAtUtc: DateTimeOffset.Parse("2026-05-15T00:00:00Z"));

        using (var conn = OpenConnection())
        {
            var catalog = new SqliteVectorCatalog(conn);
            catalog.CreateIndex(MakeSpec());
            catalog.UpsertTask(record);
        }
        using (var conn = OpenConnection())
        {
            var catalog = new SqliteVectorCatalog(conn);
            var loaded = catalog.GetTask(record.Key);
            loaded.Should().NotBeNull();
            loaded!.State.Should().Be(EmbeddingTaskState.Completed);
            loaded.ContentHash.Should().Be("hash-1");
            loaded.LastUpdatedAtUtc.Should().Be(record.LastUpdatedAtUtc);
        }
    }

    [Fact]
    public void Upsert_overwrites_existing_task_state()
    {
        using var conn = OpenConnection();
        var catalog = new SqliteVectorCatalog(conn);
        catalog.CreateIndex(MakeSpec());

        var key = new EmbeddingTaskKey(EntityKind.Node, 1, "doc-embed", "prov");
        catalog.UpsertTask(new EmbeddingTaskRecord(
            key.EntityKind, key.EntityId, key.IndexName, key.ProviderId,
            EmbeddingTaskState.InProgress, null, null,
            DateTimeOffset.Parse("2026-05-14T00:00:00Z")));
        catalog.UpsertTask(new EmbeddingTaskRecord(
            key.EntityKind, key.EntityId, key.IndexName, key.ProviderId,
            EmbeddingTaskState.Completed, "h", null,
            DateTimeOffset.Parse("2026-05-15T00:00:00Z")));

        var current = catalog.GetTask(key);
        current!.State.Should().Be(EmbeddingTaskState.Completed);
        current.ContentHash.Should().Be("h");
        catalog.ListTasks().Should().ContainSingle();
    }

    [Fact]
    public void Dropping_index_cascades_to_tasks()
    {
        using var conn = OpenConnection();
        var catalog = new SqliteVectorCatalog(conn);
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
    }

    [Fact]
    public void ListTasks_filters_by_index_name()
    {
        using var conn = OpenConnection();
        var catalog = new SqliteVectorCatalog(conn);
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

    [Fact]
    public void Schema_bootstrap_is_idempotent()
    {
        // Bootstrap runs on every Open; running it twice on the same file
        // must not double-create tables or drop data.
        using (var conn = OpenConnection())
        {
            var catalog = new SqliteVectorCatalog(conn);
            catalog.CreateIndex(MakeSpec("survives"));
        }
        using (var conn = OpenConnection())   // Bootstrap runs again here.
        {
            var catalog = new SqliteVectorCatalog(conn);
            catalog.TryGetIndex("survives", out _).Should().BeTrue();
        }
    }
}

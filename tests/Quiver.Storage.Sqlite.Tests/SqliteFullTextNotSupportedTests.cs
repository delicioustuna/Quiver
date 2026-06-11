using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Storage.Sqlite.Tests;

/// <summary>
/// FTS-2 (decision B): full-text indexes are a binary-backend-only MVP. The SQLite
/// backend rejects CreateFullTextIndex and reports an empty full-text index list.
/// </summary>
public sealed class SqliteFullTextNotSupportedTests : IDisposable
{
    private readonly string _dir;

    public SqliteFullTextNotSupportedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sqlite_fts_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private GraphDatabase Open() => GraphDatabase.Open(_dir, new GraphDatabaseOptions
    {
        BackendFactory = new SqliteGraphStorageBackendFactory(),
    });

    [Fact]
    public void CreateFullTextIndex_throws_not_supported()
    {
        using var db = Open();
        Action act = () => db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void ListFullTextIndexes_is_empty()
    {
        using var db = Open();
        db.Schema.ListFullTextIndexes().Should().BeEmpty();
    }
}

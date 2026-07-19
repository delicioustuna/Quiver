using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// derived segmentを保持しない再起動後もprimary propertyから検索状態を復元できることを検証する。
/// </summary>
public sealed class FullTextOrphanSweepTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public FullTextOrphanSweepTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts2_orphan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Reopen_rebuilds_search_from_live_primary_properties()
    {
        VertexId live;
        using (var db = QuiverDatabase.Open(_path))
        {
            db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(
                "idx_body",
                new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
            using var tx = db.BeginWriteTransaction();
            live = tx.CreateVertex("Doc");
            tx.SetProperty(live, "body", PropertyValue.FromString("kept term"));
            tx.Commit();
        }

        using var reopened = QuiverDatabase.Open(_path);
        using var read = reopened.BeginReadTransaction();
        read.Query.Search("idx_body", "kept", 10).ToList()
            .Should().ContainSingle().Which.Should().Be(live);
    }
}

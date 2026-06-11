using FluentAssertions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FTS-2 (increment 2): the public ISchemaApi.CreateFullTextIndex surface and
/// its catalog persistence across reopen (binary backend).
/// </summary>
public sealed class FullTextIndexSchemaTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public FullTextIndexSchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts2_schema_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CreateFullTextIndex_is_listed_with_defaults()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");

        db.Schema.ListFullTextIndexes().Should().ContainSingle()
            .Which.Should().Be(new FullTextIndexInfo("idx_body", "Doc", "body", "mixed-bigram-v1"));
    }

    [Fact]
    public void CreateFullTextIndex_records_custom_tokenizer_id()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body",
            new FullTextIndexOptions { TokenizerId = "mixed-bigram-v1", K1 = 1.5, B = 0.5 });

        db.Schema.ListFullTextIndexes()[0].TokenizerId.Should().Be("mixed-bigram-v1");
    }

    [Fact]
    public void Full_text_index_survives_reopen()
    {
        using (var db = GraphDatabase.Open(_path))
        {
            db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");
        }

        using var reopened = GraphDatabase.Open(_path);
        reopened.Schema.ListFullTextIndexes().Should().ContainSingle()
            .Which.Name.Should().Be("idx_body");
    }
}

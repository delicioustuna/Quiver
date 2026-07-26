using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Text;
using Xunit;

namespace Quiver.Tests.Text;

/// <summary>
/// <see cref="FilteredTokenizer"/> を <see cref="FullTextIndexDefinition.Filters"/> から
/// 全文インデックス処理へ接続する経路をエンドツーエンドに検証する。
/// </summary>
public sealed class FilteredTokenizerIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public FilteredTokenizerIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_filt_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void StopWordFilter_excludes_terms_from_index()
    {
        _db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(
            "idx",
            new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"),
            Filters: [new StopWordFilter(["the", "a", "is", "of"])])));

        using (var tx = _db.BeginWriteTransaction())
        {
            var n1 = tx.CreateVertex("Doc");
            tx.SetProperty(n1, "body", PropertyValue.FromString("the quick brown fox"));
            var n2 = tx.CreateVertex("Doc");
            tx.SetProperty(n2, "body", PropertyValue.FromString("a lazy dog is sleeping"));
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Search("idx", "quick", 10).ToList().Should().HaveCount(1);
        g.Search("idx", "the", 10).ToList().Should().BeEmpty();
        g.Search("idx", "is", 10).ToList().Should().BeEmpty();
    }

    [Fact]
    public void FullTextIndexDefinition_reports_composite_tokenizer_id()
    {
        _db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(
            "idx2",
            new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"),
            Filters: [new LowercaseFilter(), new StopWordFilter(["x"])])));

        var indexes = _db.Schema.ListIndexes()
            .Where(static index => index.Definition is FullTextIndexDefinition)
            .ToArray();
        indexes.Should().ContainSingle(i => i.Name == "idx2");
        ((FullTextIndexDefinition)indexes[0].Definition)
            .TokenizerId.Should().Be("mixed-bigram-unigram-v1+lowercase-v1+stopwords-v1");
    }

    [Fact]
    public void StopWordFilter_is_reconstructed_after_reopen()
    {
        string path = Path.Combine(_dir, "reopen.quiver");
        using (var database = QuiverDatabase.Open(path))
        {
            database.EditSchema(schema => schema.CreateIndex(
                new FullTextIndexDefinition(
                    "reopen_idx",
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        "body",
                        "Doc"),
                    Filters: [new StopWordFilter(["the"])])));
            using var write = database.BeginWriteTransaction();
            VertexId document = write.CreateVertex("Doc");
            write.SetProperty(
                document,
                "body",
                PropertyValue.FromString("the durable text"));
            write.Commit();
        }

        using var reopened = QuiverDatabase.Open(path);
        using var read = reopened.BeginReadTransaction();
        read.Query.Search("reopen_idx", "the", 10).ToList().Should().BeEmpty();
        read.Query.Search("reopen_idx", "durable", 10).ToList()
            .Should().ContainSingle();
    }
}

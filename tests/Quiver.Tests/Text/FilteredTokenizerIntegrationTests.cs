using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Text;
using Xunit;

namespace Quiver.Tests.Text;

/// <summary>
/// <see cref="FilteredTokenizer"/> を <see cref="FullTextIndexOptions.Filters"/> から
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
        _db.Schema.CreateFullTextIndex("idx", "Doc", "body", new FullTextIndexOptions
        {
            Filters = [new StopWordFilter(["the", "a", "is", "of"])],
        });

        using (var tx = _db.BeginTransaction())
        {
            var n1 = tx.CreateVertex("Doc");
            tx.SetProperty(n1, "body", PropertyValue.FromString("the quick brown fox"));
            var n2 = tx.CreateVertex("Doc");
            tx.SetProperty(n2, "body", PropertyValue.FromString("a lazy dog is sleeping"));
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Search("idx", "quick", 10).ToList().Should().HaveCount(1);
        g.Search("idx", "the", 10).ToList().Should().BeEmpty();
        g.Search("idx", "is", 10).ToList().Should().BeEmpty();
    }

    [Fact]
    public void FullTextIndexInfo_reports_composite_tokenizer_id()
    {
        _db.Schema.CreateFullTextIndex("idx2", "Doc", "body", new FullTextIndexOptions
        {
            Filters = [new LowercaseFilter(), new StopWordFilter(["x"])],
        });

        var indexes = _db.Schema.ListFullTextIndexes();
        indexes.Should().ContainSingle(i => i.Name == "idx2");
        indexes[0].TokenizerId.Should().Be("mixed-bigram-unigram-v1+lowercase-v1+stopwords-v1");
    }
}

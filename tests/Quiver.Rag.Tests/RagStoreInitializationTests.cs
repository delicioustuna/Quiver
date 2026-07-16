using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Rag.Tests;

/// <summary>
/// RagStore 初期化の冪等性。コンストラクタが索引を冪等作成し、複数回 / reopen を跨いでも
/// 安全であることを検証する。
/// </summary>
public sealed class RagStoreInitializationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public RagStoreInitializationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_rag1_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static RagStoreOptions Options(int dim = 8) => new() { EmbeddingDimensions = dim };

    [Fact]
    public void Constructor_creates_expected_indexes()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = new RagStore(db, Options());

        // sourceId 一意キー索引。
        db.Schema.IndexExists(RagSchema.DocSourceIndex).Should().BeTrue();

        // ベクトル索引 (次元・尺度が options どおり)。
        db.Vectors.TryGetIndex(RagSchema.ChunkVectorIndex, out var spec).Should().BeTrue();
        spec.Dimensions.Should().Be(8);
        spec.Metric.Should().Be(DistanceMetric.Cosine);
        spec.EntityKind.Should().Be(EntityKind.Vertex);

        // 全文索引 (binary backend は対応)。
        store.FullTextEnabled.Should().BeTrue();
        db.Schema.ListFullTextIndexes()
            .Select(i => i.Name).Should().Contain(RagSchema.ChunkTextIndex);
    }

    [Fact]
    public void Constructing_twice_on_same_db_is_safe()
    {
        using var db = QuiverDatabase.Open(_path);

        var first = new RagStore(db, Options());
        var second = new RagStore(db, Options());   // 2 回目でも throw しない

        second.FullTextEnabled.Should().BeTrue();

        // 索引は重複作成されず 1 件ずつ。
        db.Schema.ListIndexes().Count(i => i.Name == RagSchema.DocSourceIndex).Should().Be(1);
        db.Schema.ListFullTextIndexes().Count(i => i.Name == RagSchema.ChunkTextIndex).Should().Be(1);
    }

    [Fact]
    public void Reopening_persisted_db_is_idempotent()
    {
        using (var db = QuiverDatabase.Open(_path))
        {
            _ = new RagStore(db, Options());
        }

        // 別セッションで開き直しても既存索引を踏んで no-op になる。
        using (var db = QuiverDatabase.Open(_path))
        {
            var store = new RagStore(db, Options());
            store.FullTextEnabled.Should().BeTrue();
            db.Schema.IndexExists(RagSchema.DocSourceIndex).Should().BeTrue();
            db.Vectors.TryGetIndex(RagSchema.ChunkVectorIndex, out _).Should().BeTrue();
            db.Schema.ListFullTextIndexes().Count(i => i.Name == RagSchema.ChunkTextIndex).Should().Be(1);
        }
    }

    [Fact]
    public void FullText_can_be_disabled_via_options()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = new RagStore(db, Options() with { EnableFullTextIndex = false });

        store.FullTextEnabled.Should().BeFalse();
        db.Schema.ListFullTextIndexes().Select(i => i.Name)
            .Should().NotContain(RagSchema.ChunkTextIndex);
        // ベクトル索引は引き続き作られる。
        db.Vectors.TryGetIndex(RagSchema.ChunkVectorIndex, out _).Should().BeTrue();
    }

    [Fact]
    public void Custom_vector_index_name_and_dimensions_are_honored()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = new RagStore(db, new RagStoreOptions
        {
            EmbeddingDimensions = 16,
            VectorIndexName = "my_embeddings",
            VectorMetric = DistanceMetric.Dot,
        });

        store.VectorIndexName.Should().Be("my_embeddings");
        db.Vectors.TryGetIndex("my_embeddings", out var spec).Should().BeTrue();
        spec.Dimensions.Should().Be(16);
        spec.Metric.Should().Be(DistanceMetric.Dot);
    }

    [Fact]
    public void Reopen_with_mismatched_dimensions_throws()
    {
        using (var db = QuiverDatabase.Open(_path))
            _ = new RagStore(db, Options(dim: 8));

        using (var db = QuiverDatabase.Open(_path))
        {
            var act = () => new RagStore(db, Options(dim: 16));
            act.Should().Throw<InvalidOperationException>().WithMessage("*次元*");
        }
    }

    [Fact]
    public void Reopen_with_mismatched_metric_throws()
    {
        using (var db = QuiverDatabase.Open(_path))
            _ = new RagStore(db, new RagStoreOptions { EmbeddingDimensions = 8, VectorMetric = DistanceMetric.Cosine });

        using (var db = QuiverDatabase.Open(_path))
        {
            var act = () => new RagStore(db, new RagStoreOptions { EmbeddingDimensions = 8, VectorMetric = DistanceMetric.Dot });
            act.Should().Throw<InvalidOperationException>().WithMessage("*距離尺度*");
        }
    }

    [Fact]
    public void Existing_fulltext_index_stays_enabled_even_when_option_disabled()
    {
        // セッション 1: 全文索引を作る。
        using (var db = QuiverDatabase.Open(_path))
        {
            var store = new RagStore(db, Options());
            store.FullTextEnabled.Should().BeTrue();
        }

        // セッション 2: EnableFullTextIndex=false で開いても、既存索引があるので利用可能。
        using (var db = QuiverDatabase.Open(_path))
        {
            var store = new RagStore(db, Options() with { EnableFullTextIndex = false });
            store.FullTextEnabled.Should().BeTrue();
            db.Schema.ListFullTextIndexes().Count(i => i.Name == RagSchema.ChunkTextIndex).Should().Be(1);
        }
    }

    [Fact]
    public void Invalid_dimensions_throw()
    {
        using var db = QuiverDatabase.Open(_path);
        var act = () => new RagStore(db, new RagStoreOptions { EmbeddingDimensions = 0 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Null_arguments_throw()
    {
        using var db = QuiverDatabase.Open(_path);
        ((Action)(() => new RagStore(null!, Options()))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new RagStore(db, null!))).Should().Throw<ArgumentNullException>();
    }
}

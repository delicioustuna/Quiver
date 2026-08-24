using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Text;
using Xunit;

namespace Yatagarasu.Rag.Tests;

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
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_rag1_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.yata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static RagStoreOptions Options(int dim = 8) => new()
    {
        EmbeddingDimensions = dim,
        IngestionProfile = new RagIngestionProfile
        {
            EmbeddingProfileId = "fake-embedding-v1",
        },
    };

    [Fact]
    public void Constructor_creates_expected_indexes()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = new RagStore(db, Options());

        // sourceId 一意キー索引。
        db.Schema.IndexExists(RagSchema.DocSourceIndex).Should().BeTrue();

        // ベクトル索引 (次元・尺度が options どおり)。
        db.Schema.TryGetIndex(RagSchema.ChunkVectorIndex, out var info).Should().BeTrue();
        var definition = info.Definition.Should().BeOfType<VectorIndexDefinition>().Subject;
        definition.Dimensions.Should().Be(8);
        definition.Metric.Should().Be(DistanceMetric.Cosine);
        definition.Target.OwnerKind.Should().Be(PropertyOwnerKind.Vertex);

        // 全文索引 (binary backend は対応)。
        store.FullTextEnabled.Should().BeTrue();
        db.Schema.ListIndexes().Where(i => i.Definition is FullTextIndexDefinition).ToArray()
            .Select(i => i.Name).Should().Contain(RagSchema.ChunkTextIndex);
    }

    [Fact]
    public void Constructing_twice_on_same_db_is_safe()
    {
        using var db = YatagarasuDatabase.Open(_path);

        var first = new RagStore(db, Options());
        var second = new RagStore(db, Options());   // 2 回目でも throw しない

        second.FullTextEnabled.Should().BeTrue();

        // 索引は重複作成されず 1 件ずつ。
        db.Schema.ListIndexes().Count(i => i.Name == RagSchema.DocSourceIndex).Should().Be(1);
        db.Schema.ListIndexes().Where(i => i.Definition is FullTextIndexDefinition).ToArray().Count(i => i.Name == RagSchema.ChunkTextIndex).Should().Be(1);
    }

    [Fact]
    public void Reopening_persisted_db_is_idempotent()
    {
        using (var db = YatagarasuDatabase.Open(_path))
        {
            _ = new RagStore(db, Options());
        }

        // 別セッションで開き直しても既存索引を踏んで no-op になる。
        using (var db = YatagarasuDatabase.Open(_path))
        {
            var store = new RagStore(db, Options());
            store.FullTextEnabled.Should().BeTrue();
            db.Schema.IndexExists(RagSchema.DocSourceIndex).Should().BeTrue();
            db.Schema.TryGetIndex(RagSchema.ChunkVectorIndex, out _).Should().BeTrue();
            db.Schema.ListIndexes().Where(i => i.Definition is FullTextIndexDefinition).ToArray().Count(i => i.Name == RagSchema.ChunkTextIndex).Should().Be(1);
        }
    }

    [Fact]
    public void FullText_can_be_disabled_via_options()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = new RagStore(db, Options() with { EnableFullTextIndex = false });

        store.FullTextEnabled.Should().BeFalse();
        db.Schema.ListIndexes().Where(i => i.Definition is FullTextIndexDefinition).ToArray().Select(i => i.Name)
            .Should().NotContain(RagSchema.ChunkTextIndex);
        // ベクトル索引は引き続き作られる。
        db.Schema.TryGetIndex(RagSchema.ChunkVectorIndex, out _).Should().BeTrue();
    }

    [Fact]
    public void Japanese_variant_expansion_is_explicitly_configurable_for_rag()
    {
        using var db = YatagarasuDatabase.Open(_path);
        _ = new RagStore(db, Options() with
        {
            FullTextFilters = [new JapaneseOrthographicVariantFilter()],
        });

        FullTextIndexDefinition definition = db.Schema.ListIndexes()
            .Select(static index => index.Definition)
            .OfType<FullTextIndexDefinition>()
            .Should().ContainSingle().Which;
        definition.Filters.Should().ContainSingle()
            .Which.Should().BeOfType<JapaneseOrthographicVariantFilter>();

        using (var write = db.BeginWriteTransaction())
        {
            VertexId chunk = write.CreateVertex(RagSchema.ChunkLabel);
            write.SetProperty(
                chunk,
                RagSchema.PropSearchText,
                PropertyValue.FromString("渡邉直美"));
            write.Commit();
        }

        using var read = db.BeginReadTransaction();
        read.Query.Search(RagSchema.ChunkTextIndex, "渡辺", 10).ToList()
            .Should().ContainSingle();
    }

    [Fact]
    public void Reopen_with_different_fulltext_expansion_is_rejected()
    {
        using var db = YatagarasuDatabase.Open(_path);
        RagStoreOptions configured = Options() with
        {
            FullTextFilters =
            [
                new JapaneseOrthographicVariantFilter(
                    new Dictionary<char, char> { ['邉'] = '辺' }),
            ],
        };
        _ = new RagStore(db, configured);

        var reopenWithSameMapping = () => new RagStore(db, configured);
        reopenWithSameMapping.Should().NotThrow();

        var act = () => new RagStore(db, Options() with
        {
            FullTextFilters =
            [
                new JapaneseOrthographicVariantFilter(
                    new Dictionary<char, char> { ['髙'] = '高' }),
            ],
        });

        act.Should().Throw<RagIngestionProfileMismatchException>();
    }

    [Fact]
    public void Custom_vector_index_name_and_dimensions_are_honored()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = new RagStore(db, new RagStoreOptions
        {
            EmbeddingDimensions = 16,
            IngestionProfile = new RagIngestionProfile
            {
                EmbeddingProfileId = "fake-embedding-v1",
            },
            VectorIndexName = "my_embeddings",
            VectorMetric = DistanceMetric.Dot,
        });

        store.VectorIndexName.Should().Be("my_embeddings");
        db.Schema.TryGetIndex("my_embeddings", out var info).Should().BeTrue();
        var definition = info.Definition.Should().BeOfType<VectorIndexDefinition>().Subject;
        definition.Dimensions.Should().Be(16);
        definition.Metric.Should().Be(DistanceMetric.Dot);
    }

    [Fact]
    public void Reopen_with_mismatched_dimensions_throws()
    {
        using (var db = YatagarasuDatabase.Open(_path))
            _ = new RagStore(db, Options(dim: 8));

        using (var db = YatagarasuDatabase.Open(_path))
        {
            var act = () => new RagStore(db, Options(dim: 16));
            act.Should().Throw<RagIngestionProfileMismatchException>();
        }
    }

    [Fact]
    public void Reopen_with_mismatched_metric_throws()
    {
        using (var db = YatagarasuDatabase.Open(_path))
            _ = new RagStore(db, Options() with { VectorMetric = DistanceMetric.Cosine });

        using (var db = YatagarasuDatabase.Open(_path))
        {
            var act = () => new RagStore(db, Options() with { VectorMetric = DistanceMetric.Dot });
            act.Should().Throw<RagIngestionProfileMismatchException>();
        }
    }

    [Fact]
    public void Reopen_with_mismatched_chunking_profile_is_rejected()
    {
        using var db = YatagarasuDatabase.Open(_path);
        _ = new RagStore(db, Options() with
        {
            Chunking = new ChunkingOptions { TargetSize = 800, Overlap = 100 },
        });

        var act = () => new RagStore(db, Options() with
        {
            Chunking = new ChunkingOptions { TargetSize = 400, Overlap = 50 },
        });

        var exception = act.Should()
            .Throw<RagIngestionProfileMismatchException>()
            .Which;
        exception.StoredFingerprint.Should().NotBeEmpty();
        exception.RequestedFingerprint.Should().NotBe(exception.StoredFingerprint);
    }

    [Fact]
    public void Reopen_with_mismatched_embedding_input_template_is_rejected()
    {
        using var db = YatagarasuDatabase.Open(_path);
        _ = new RagStore(db, Options());

        var act = () => new RagStore(db, Options() with
        {
            IngestionProfile = Options().IngestionProfile with
            {
                EmbeddingInputTemplate = RagEmbeddingInputTemplate.ChunkText,
            },
        });

        act.Should().Throw<RagIngestionProfileMismatchException>();
    }

    [Fact]
    public void Legacy_corpus_requires_explicit_profile_adoption_before_schema_changes()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using (var tx = db.BeginWriteTransaction())
        {
            tx.EditSchema.GetOrCreateLabel(RagSchema.DocumentLabel);
            tx.CreateVertex(RagSchema.DocumentLabel);
            tx.Commit();
        }
        db.Schema.ListIndexes().Should().BeEmpty();

        var reject = () => new RagStore(db, Options());

        reject.Should().Throw<RagIngestionProfileMismatchException>();
        db.Schema.ListIndexes().Should().BeEmpty(
            "profile preflight は不一致の旧コーパスへ schema を追加しない");

        var promoteWithoutBackfill = () => new RagStore(db, Options() with
        {
            AdoptLegacyIngestionProfile = true,
            MetadataIndexes =
            [
                new RagMetadataIndex("category", "ragMetadata.category", "idx_rag_metadata_category"),
            ],
        });
        promoteWithoutBackfill.Should().Throw<RagIngestionProfileMismatchException>();
        db.Schema.ListIndexes().Should().BeEmpty();

        var adopted = new RagStore(db, Options() with
        {
            AdoptLegacyIngestionProfile = true,
        });
        adopted.Options.IngestionProfile.EmbeddingProfileId
            .Should().Be("fake-embedding-v1");

        var reopen = () => new RagStore(db, Options());
        reopen.Should().NotThrow();
    }

    [Fact]
    public void Existing_fulltext_index_stays_enabled_even_when_option_disabled()
    {
        // セッション 1: 全文索引を作る。
        using (var db = YatagarasuDatabase.Open(_path))
        {
            var store = new RagStore(db, Options());
            store.FullTextEnabled.Should().BeTrue();
        }

        // セッション 2: EnableFullTextIndex=false で開いても、既存索引があるので利用可能。
        using (var db = YatagarasuDatabase.Open(_path))
        {
            var store = new RagStore(db, Options() with { EnableFullTextIndex = false });
            store.FullTextEnabled.Should().BeTrue();
            db.Schema.ListIndexes().Where(i => i.Definition is FullTextIndexDefinition).ToArray().Count(i => i.Name == RagSchema.ChunkTextIndex).Should().Be(1);
        }
    }

    [Fact]
    public void Invalid_dimensions_throw()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var act = () => new RagStore(db, Options(dim: 0));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Null_arguments_throw()
    {
        using var db = YatagarasuDatabase.Open(_path);
        ((Action)(() => new RagStore((GraphStore)null!, Options()))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new RagStore(db, null!))).Should().Throw<ArgumentNullException>();
    }
}

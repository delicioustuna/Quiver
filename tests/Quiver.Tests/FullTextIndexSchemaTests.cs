using FluentAssertions;
using Quiver.Index.FullText;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 公開 API <c>ISchemaEditor.CreateIndex</c> と、
/// バイナリバックエンドを再オープンした後のカタログ永続化を検証する。
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
    public void Unified_definition_is_listed_with_defaults()
    {
        using var db = QuiverDatabase.Open(_path);
        db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition("idx_body", new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));

        IndexInfo info = db.Schema.ListIndexes().Should().ContainSingle().Which;
        var definition = info.Definition.Should().BeOfType<FullTextIndexDefinition>().Subject;
        definition.Name.Should().Be("idx_body");
        definition.Target.Should().Be(new PropertyTarget(
            PropertyOwnerKind.Vertex,
            "body",
            "Doc"));
    }

    [Fact]
    public void Definition_options_survive_reopen()
    {
        using (var db = QuiverDatabase.Open(_path))
        {
            db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(
                "idx_body",
                new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"),
                TokenizerId: "mixed-bigram-v1",
                K1: 1.5,
                B: 0.5,
                SegmentPolicy: new(
                    MaximumDeltaEntries: 17,
                    MaximumSegments: 3,
                    MaximumTombstoneRatio: 0.2))));
        }

        using var reopened = QuiverDatabase.Open(_path);
        var definition =
            (FullTextIndexDefinition)reopened.Schema.ListIndexes()[0].Definition;
        definition.TokenizerId.Should().Be("mixed-bigram-v1");
        definition.K1.Should().Be(1.5);
        definition.B.Should().Be(0.5);
        definition.SegmentPolicy.Should().Be(new FullTextSegmentPolicy(17, 3, 0.2));
    }

    [Fact]
    public void Full_text_index_survives_reopen()
    {
        using (var db = QuiverDatabase.Open(_path))
        {
            db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition("idx_body", new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
        }

        using var reopened = QuiverDatabase.Open(_path);
        reopened.Schema.ListIndexes().Should().ContainSingle()
            .Which.Name.Should().Be("idx_body");
    }

    [Theory]
    [InlineData("1:Doc")]
    [InlineData("Doc")]
    public void Definition_codec_rejects_non_current_payload(string encoded)
    {
        Action decode = () => FullTextDefinitionCodec.Decode(
            "idx_body",
            encoded,
            "body",
            "mixed-bigram-v1");

        decode.Should().Throw<InvalidDataException>()
            .WithMessage("*current qft2*");
    }
}

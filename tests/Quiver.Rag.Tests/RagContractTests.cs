using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Quiver.Rag.Tests;

/// <summary>
/// RAG-5: 取込側 (PdfTools 等) との契約固定。契約の正本は <see cref="IngestedDocumentJson"/>
/// (Quiver.Rag 本体)。この approval test はフィクスチャがその正本どおりに復元でき、往復が無損失で、
/// シリアライズ規約 (camelCase / 文字列 enum / null 省略) が守られていることを監視する。
/// </summary>
public sealed class RagContractTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ingested_document.json");

    private static string ReadFixture() => File.ReadAllText(FixturePath);

    [Fact]
    public void Fixture_deserializes_to_expected_contract()
    {
        var doc = IngestedDocumentJson.Deserialize(ReadFixture());

        doc.Should().NotBeNull();
        doc!.SourceId.Should().Be("pdftools/report-2026.pdf");
        doc.Title.Should().Be("Annual Report 2026");
        doc.Metadata.Should().Contain("author", "Finance Team");
        doc.Metadata.Should().Contain("lang", "en");

        doc.Blocks.Should().HaveCount(4);
        doc.Blocks[0].Kind.Should().Be(BlockKind.Heading);
        doc.Blocks[0].HeadingLevel.Should().Be(1);
        doc.Blocks[0].Page.Should().Be(1);
        doc.Blocks[1].Kind.Should().Be(BlockKind.Paragraph);
        doc.Blocks[1].HeadingLevel.Should().BeNull();
        doc.Blocks[2].Kind.Should().Be(BlockKind.Table);
        doc.Blocks[2].Page.Should().BeNull();
        doc.Blocks[3].Kind.Should().Be(BlockKind.Code);
    }

    [Fact]
    public void Round_trip_is_lossless()
    {
        var doc = IngestedDocumentJson.Deserialize(ReadFixture())!;

        var doc2 = IngestedDocumentJson.Deserialize(IngestedDocumentJson.Serialize(doc))!;

        doc2.SourceId.Should().Be(doc.SourceId);
        doc2.Title.Should().Be(doc.Title);
        doc2.Metadata.Should().BeEquivalentTo(doc.Metadata);
        doc2.Blocks.Should().HaveCount(doc.Blocks.Count);
        for (int i = 0; i < doc.Blocks.Count; i++)
        {
            doc2.Blocks[i].Kind.Should().Be(doc.Blocks[i].Kind);
            doc2.Blocks[i].Text.Should().Be(doc.Blocks[i].Text);
            doc2.Blocks[i].HeadingLevel.Should().Be(doc.Blocks[i].HeadingLevel);
            doc2.Blocks[i].Page.Should().Be(doc.Blocks[i].Page);
        }
    }

    [Fact]
    public void Serialized_form_uses_camelCase_string_enums_and_omits_nulls()
    {
        var doc = IngestedDocumentJson.Deserialize(ReadFixture())!;
        var json = IngestedDocumentJson.Serialize(doc);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        root.TryGetProperty("sourceId", out _).Should().BeTrue("camelCase プロパティ名");
        var blocks = root.GetProperty("blocks");
        blocks[0].GetProperty("kind").GetString().Should().Be("Heading", "enum は文字列");
        // null フィールドは省略される (キー自体が現れない)。
        blocks[1].TryGetProperty("headingLevel", out _).Should().BeFalse("Paragraph に headingLevel は出ない");
        blocks[2].TryGetProperty("page", out _).Should().BeFalse("Table に page は出ない");
    }
}

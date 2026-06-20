using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiver.Rag;

/// <summary>
/// <see cref="IngestedDocument"/> の正準 JSON 契約。取込側 (PdfTools 等) と Quiver.Rag の境界の正本。
/// 規約: camelCase プロパティ名 / <see cref="BlockKind"/> は文字列 / null フィールドは省略。
/// </summary>
/// <remarks>
/// source-generated <see cref="JsonSerializerContext"/> を使うため NativeAOT / trim 安全
/// (反射ベースのシリアライズを持ち込まない)。
/// 取込側はこの <see cref="Serialize"/> / <see cref="Deserialize"/> (または <see cref="Options"/>) を参照することで規約のドリフトを防げる。
/// </remarks>
public static class IngestedDocumentJson
{
    /// <summary>契約の <see cref="JsonSerializerOptions"/> (source-gen resolver 付き)。</summary>
    public static JsonSerializerOptions Options => IngestedDocumentJsonContext.Default.Options;

    /// <summary>文書を契約 JSON へ直列化する。</summary>
    public static string Serialize(IngestedDocument document)
        => JsonSerializer.Serialize(document, IngestedDocumentJsonContext.Default.IngestedDocument);

    /// <summary>契約 JSON から文書を復元する。</summary>
    public static IngestedDocument? Deserialize(string json)
        => JsonSerializer.Deserialize(json, IngestedDocumentJsonContext.Default.IngestedDocument);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(IngestedDocument))]
internal sealed partial class IngestedDocumentJsonContext : JsonSerializerContext;

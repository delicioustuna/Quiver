namespace Quiver.Rag;

/// <summary>
/// 利用者が選んだ string metadata を Document の独立 property と scalar index へ昇格する定義。
/// <see cref="RagSearchOptions.MetadataEquals"/> は該当キーの index seek を候補文書の入口に使う。
/// </summary>
/// <param name="MetadataKey"><see cref="IngestedDocument.Metadata"/> 内の論理キー。</param>
/// <param name="PropertyKey">Document に保存する property key。</param>
/// <param name="IndexName">作成する StringEquality scalar index 名。</param>
public sealed record RagMetadataIndex(
    string MetadataKey,
    string PropertyKey,
    string IndexName);

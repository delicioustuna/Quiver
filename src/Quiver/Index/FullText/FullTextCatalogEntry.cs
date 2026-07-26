namespace Quiver.Index.FullText;

/// <summary>
/// 全文derived indexの永続definitionとimmutable segment manifest参照。
/// </summary>
internal sealed record FullTextCatalogEntry(
    string Name,
    string Target,
    string PropertyKey,
    string TokenizerId,
    IndexLifecycleState State,
    string Manifest);

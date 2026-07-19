namespace Quiver.Index.FullText;

/// <summary>
/// 全文derived indexの永続definitionとimmutable segment manifest参照。
/// legacy tenant IDは旧formatを安全に開き、再利用を避けるためだけに保持する。
/// </summary>
internal sealed record FullTextCatalogEntry(
    string Name,
    string Target,
    string PropertyKey,
    string TokenizerId,
    byte LegacyPostingsTenantId,
    byte LegacyNormsTenantId,
    IndexLifecycleState State,
    string Manifest);

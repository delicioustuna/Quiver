namespace Yatagarasu.Core;

/// <summary>
/// エンティティに属するプロパティキーの論理アドレス。
/// 同じキーでも所有者が異なれば別のプロパティとして扱われます。
/// </summary>
/// <param name="Owner">プロパティを所有する Vertex、Edge、または Nexus。</param>
/// <param name="Key">プロパティキー。</param>
internal readonly record struct PropertyAddress(EntityRef Owner, PropertyKeyId Key)
{
    /// <summary>有効な所有者とキーを持つかどうか。</summary>
    public bool IsValid => Owner.IsValid && Key.IsValid;
}

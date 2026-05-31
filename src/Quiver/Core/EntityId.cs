namespace Quiver.Core;

/// <summary>
/// <see cref="EntityId"/> がどのエンティティ種別を指すかを示すタグ。
/// ベクトルストア (VEC-1; codex_advice_3.md 6.2 節 — Node / Relationship を利用) と、
/// 内部タグ付き ID API (FT-11; codex_advice_3.md 7.1 節 — 診断 / カタログ用に Property を追加) で共有する。
/// </summary>
public enum EntityKind : byte
{
    /// <summary>ノード。</summary>
    Node = 1,
    /// <summary>リレーションシップ。</summary>
    Relationship = 2,
    /// <summary>プロパティ。</summary>
    Property = 3,
}

/// <summary>
/// <see cref="NodeId"/>、<see cref="RelationshipId"/>、<see cref="PropertyId"/> を統一して扱うための
/// 内部タグ付き識別子。診断、オペレータ配線、将来のカタログ用途を想定。
/// オンディスクフォーマットには含まれない — シリアライズする場合は事前にフォーマットバージョンバイトを導入すること。
/// </summary>
public readonly record struct EntityId(EntityKind Kind, long LocalId)
{
    /// <summary>無効値を表す sentinel。</summary>
    public static readonly EntityId Invalid = new((EntityKind)0, -1);

    /// <summary>有効な ID なら true。</summary>
    public bool IsValid => Kind != 0 && LocalId >= 0;

    /// <summary><see cref="NodeId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromNode(NodeId id) => new(EntityKind.Node, id.Value);

    /// <summary><see cref="RelationshipId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromRelationship(RelationshipId id) => new(EntityKind.Relationship, id.Value);

    /// <summary><see cref="PropertyId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromProperty(PropertyId id) => new(EntityKind.Property, id.Value);

    /// <summary>ノードとして取り出す。種別不一致なら <see cref="InvalidOperationException"/>。</summary>
    public NodeId AsNode()
    {
        if (Kind != EntityKind.Node)
            throw new InvalidOperationException($"EntityId は {Kind} であり、Node ではありません。");
        return new NodeId(LocalId);
    }

    /// <summary>リレーションシップとして取り出す。種別不一致なら例外。</summary>
    public RelationshipId AsRelationship()
    {
        if (Kind != EntityKind.Relationship)
            throw new InvalidOperationException($"EntityId は {Kind} であり、Relationship ではありません。");
        return new RelationshipId(LocalId);
    }

    /// <summary>プロパティとして取り出す。種別不一致なら例外。</summary>
    public PropertyId AsProperty()
    {
        if (Kind != EntityKind.Property)
            throw new InvalidOperationException($"EntityId は {Kind} であり、Property ではありません。");
        return new PropertyId(LocalId);
    }

    /// <summary>
    /// 64 ビット値にパックする: 上位 4 ビット = <see cref="EntityKind"/>、下位 60 ビット = LocalId。
    /// LocalId は 60 ビット (0..2^60-1) に収まるか、-1 (Invalid) でなければならない。
    /// インメモリ診断専用で、バージョン付きフォーマットヘッダ無しでの永続化は禁止。
    /// </summary>
    public ulong ToPacked()
    {
        if (!IsValid) return 0UL;
        if ((ulong)LocalId > (1UL << 60) - 1)
            throw new InvalidOperationException($"LocalId {LocalId} は 60 ビットに収まりません。");
        return ((ulong)(byte)Kind << 60) | (ulong)LocalId;
    }

    /// <summary>パック済み 64 ビット値から <see cref="EntityId"/> を復元する。</summary>
    public static EntityId FromPacked(ulong packed)
    {
        if (packed == 0UL) return Invalid;
        var kind = (EntityKind)(byte)(packed >> 60);
        var local = (long)(packed & ((1UL << 60) - 1));
        return new EntityId(kind, local);
    }

    /// <inheritdoc/>
    public override string ToString() => IsValid ? $"{Kind}#{LocalId}" : "Entity#Invalid";
}

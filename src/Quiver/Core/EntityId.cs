namespace Quiver.Core;

/// <summary>
/// <see cref="EntityId"/> がどのエンティティ種別を指すかを示すタグ。
/// ベクトルストアと内部タグ付き ID API で共有する。
/// </summary>
public enum EntityKind : byte
{
    /// <summary>ノード。</summary>
    Node = 1,
    /// <summary>リレーションシップ。</summary>
    Relationship = 2,
    /// <summary>ハイパーエッジ。</summary>
    Hyperedge = 4,
}

/// <summary>
/// <see cref="NodeId"/>、<see cref="RelationshipId"/>、<see cref="HyperedgeId"/> を統一して扱うための
/// 内部タグ付き識別子。診断、オペレータ配線、将来のカタログ用途を想定。
/// オンディスクフォーマットには含まれない — シリアライズする場合は事前にフォーマットバージョンバイトを導入すること。
/// </summary>
internal readonly record struct EntityId(EntityKind Kind, long LocalId)
{
    private const ulong LocalMask = (1UL << EntityRef.KindShift) - 1;

    /// <summary>無効値を表す sentinel。</summary>
    public static readonly EntityId Invalid = new((EntityKind)0, -1);

    /// <summary>有効な entity kind と 60 bit の local 値を持つなら true。</summary>
    public bool IsValid => IsSupportedKind(Kind) && LocalId >= 0 && (ulong)LocalId <= LocalMask;

    /// <summary><see cref="NodeId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromNode(NodeId id) => From(EntityKind.Node, id.Value);

    /// <summary><see cref="RelationshipId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromRelationship(RelationshipId id) => From(EntityKind.Relationship, id.Value);

    /// <summary><see cref="HyperedgeId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromHyperedge(HyperedgeId id) => From(EntityKind.Hyperedge, id.Value);

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

    /// <summary>ハイパーエッジとして取り出す。種別不一致なら例外。</summary>
    public HyperedgeId AsHyperedge()
    {
        if (Kind != EntityKind.Hyperedge)
            throw new InvalidOperationException($"EntityId は {Kind} であり、Hyperedge ではありません。");
        return new HyperedgeId(LocalId);
    }

    /// <summary>
    /// 64 ビット値にパックする: 上位 4 ビット = <see cref="EntityKind"/>、下位 60 ビット = LocalId。
    /// <see cref="Invalid"/> だけは 0 へパックする。他の invalid 値は破損入力なので拒否する。
    /// インメモリ診断専用で、バージョン付きフォーマットヘッダ無しでの永続化は禁止。
    /// </summary>
    public ulong ToPacked()
    {
        if (this == Invalid) return 0UL;
        if (!IsValid)
            throw new ArgumentOutOfRangeException(nameof(LocalId), "EntityId は有効な kind と 60 bit の local value を持つ必要があります。");
        return ((ulong)(byte)Kind << 60) | (ulong)LocalId;
    }

    /// <summary>パック済み 64 ビット値から <see cref="EntityId"/> を復元する。</summary>
    public static EntityId FromPacked(ulong packed)
    {
        if (packed == 0UL) return Invalid;
        var kind = (EntityKind)(byte)(packed >> 60);
        var local = (long)(packed & ((1UL << 60) - 1));
        ValidateKind(kind);
        return new EntityId(kind, local);
    }

    /// <inheritdoc/>
    public override string ToString() => IsValid ? $"{Kind}#{LocalId}" : "Entity#Invalid";

    private static EntityId From(EntityKind kind, long localId)
    {
        ValidateKind(kind);
        if (localId == -1) return Invalid;
        if (localId < 0 || (ulong)localId > LocalMask)
            throw new ArgumentOutOfRangeException(nameof(localId), "EntityId の local value は 60 bit の非負値でなければなりません。");
        return new EntityId(kind, localId);
    }

    private static bool IsSupportedKind(EntityKind kind)
        => kind is EntityKind.Node or EntityKind.Relationship or EntityKind.Hyperedge;

    private static void ValidateKind(EntityKind kind)
    {
        if (!IsSupportedKind(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "EntityId は Node、Relationship、Hyperedge だけを受け入れます。");
    }
}

namespace Quiver.Core;

/// <summary>
/// <see cref="EntityId"/> がどのエンティティ種別を指すかを示すタグ。
/// ベクトルストアと内部タグ付き ID API で共有する。
/// </summary>
public enum EntityKind : byte
{
    /// <summary>Vertex。</summary>
    Vertex = 1,
    /// <summary>Edge。</summary>
    Edge = 2,
    /// <summary>Nexus。</summary>
    Nexus = 4,
}

/// <summary>
/// <see cref="VertexId"/>、<see cref="EdgeId"/>、<see cref="NexusId"/> を統一して扱うための
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

    /// <summary><see cref="VertexId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromVertex(VertexId id) => From(EntityKind.Vertex, id.Value);

    /// <summary><see cref="EdgeId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromEdge(EdgeId id) => From(EntityKind.Edge, id.Value);

    /// <summary><see cref="NexusId"/> からタグ付き ID を生成する。</summary>
    public static EntityId FromNexus(NexusId id) => From(EntityKind.Nexus, id.Value);

    /// <summary>Vertexとして取り出す。種別不一致なら <see cref="InvalidOperationException"/>。</summary>
    public VertexId AsVertex()
    {
        if (Kind != EntityKind.Vertex)
            throw new InvalidOperationException($"EntityId は {Kind} であり、Vertex ではありません。");
        return new VertexId(LocalId);
    }

    /// <summary>Edgeとして取り出す。種別不一致なら例外。</summary>
    public EdgeId AsEdge()
    {
        if (Kind != EntityKind.Edge)
            throw new InvalidOperationException($"EntityId は {Kind} であり、Edge ではありません。");
        return new EdgeId(LocalId);
    }

    /// <summary>Nexusとして取り出す。種別不一致なら例外。</summary>
    public NexusId AsNexus()
    {
        if (Kind != EntityKind.Nexus)
            throw new InvalidOperationException($"EntityId は {Kind} であり、Nexus ではありません。");
        return new NexusId(LocalId);
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
        => kind is EntityKind.Vertex or EntityKind.Edge or EntityKind.Nexus;

    private static void ValidateKind(EntityKind kind)
    {
        if (!IsSupportedKind(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "EntityId は Vertex、Edge、Nexus だけを受け入れます。");
    }
}

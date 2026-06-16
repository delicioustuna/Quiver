using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// On-disk: TypeId(2) | RelId(6) | NeighborId(6) = 14 bytes.
/// In-memory: natural-width fields.
/// </summary>
internal readonly struct AdjacencyEntry
{
    public readonly RelationshipTypeId Type;
    public readonly RelationshipId RelId;
    public readonly NodeId NeighborId;

    public AdjacencyEntry(RelationshipTypeId type, RelationshipId relId, NodeId neighborId)
    {
        Type = type; RelId = relId; NeighborId = neighborId;
    }
}

/// <summary>
/// inline payload lane (Int64 または Double) を持つ V2 隣接エントリ。
/// <see cref="PayloadRaw"/> の解釈は V2 ストアの <see cref="PayloadLaneSpec.Kind"/> に依存する。
/// </summary>
internal readonly struct AdjacencyEntryV2
{
    public readonly RelationshipTypeId Type;
    public readonly RelationshipId RelId;
    public readonly NodeId NeighborId;
    public readonly long PayloadRaw;

    public AdjacencyEntryV2(RelationshipTypeId type, RelationshipId relId, NodeId neighborId, long payloadRaw)
    {
        Type = type; RelId = relId; NeighborId = neighborId; PayloadRaw = payloadRaw;
    }

    public double PayloadAsDouble => BitConverter.Int64BitsToDouble(PayloadRaw);
}

/// <summary>
/// V2 payload lane に inline する値の種別。
/// </summary>
public enum PayloadKind : byte
{
    /// <summary>payload lane 無し。</summary>
    None = 0,
    /// <summary>64bit 整数を inline する。</summary>
    Int64 = 1,
    /// <summary>倍精度浮動小数点を inline する。</summary>
    Double = 2,
}

/// <summary>
/// <see cref="AdjacencyBlockStoreV2"/> に付随する任意の payload lane の設定。
/// <see cref="PropertyKeyId"/> はどのリレーションシッププロパティを inline するかを示し、
/// <see cref="DefaultRaw"/> はそのキーの値を持たない (または型が異なる) エッジに代入する生の
/// 64bit 値。既定値ポリシーはビュー構築時に固定される。
/// </summary>
public readonly struct PayloadLaneSpec
{
    /// <summary>inline する値の種別。</summary>
    public readonly PayloadKind Kind;
    /// <summary>inline 対象のリレーションシッププロパティキー ID。</summary>
    public readonly int PropertyKeyId;
    /// <summary>値が無い / 型不一致のエッジに代入する生の 64bit 既定値。</summary>
    public readonly long DefaultRaw;

    /// <summary>種別 / プロパティキー / 既定値を指定して payload lane 設定を生成する。</summary>
    public PayloadLaneSpec(PayloadKind kind, int propertyKeyId, long defaultRaw)
    {
        Kind = kind;
        PropertyKeyId = propertyKeyId;
        DefaultRaw = defaultRaw;
    }

    /// <summary>Int64 payload lane の設定を生成する。</summary>
    public static PayloadLaneSpec ForInt64(int propertyKeyId, long defaultValue = 0)
        => new(PayloadKind.Int64, propertyKeyId, defaultValue);

    /// <summary>Double payload lane の設定を生成する (既定値は double ビットで格納)。</summary>
    public static PayloadLaneSpec ForDouble(int propertyKeyId, double defaultValue = 0.0)
        => new(PayloadKind.Double, propertyKeyId, BitConverter.DoubleToInt64Bits(defaultValue));
}

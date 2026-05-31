using Quiver.Core;

namespace Quiver.Stores;

/// <summary>
/// On-disk: TypeId(2) | RelId(6) | NeighborId(6) = 14 bytes.
/// In-memory: natural-width fields.
/// </summary>
public readonly struct AdjacencyEntry
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
/// BA-6: V2 entry with an inline payload lane (Int64 or Double). The
/// interpretation of <see cref="PayloadRaw"/> depends on the V2 store's
/// <see cref="PayloadLaneSpec.Kind"/>.
/// </summary>
public readonly struct AdjacencyEntryV2
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
/// BA-6: kind of value inlined in the V2 payload lane.
/// </summary>
public enum PayloadKind : byte
{
    None = 0,
    Int64 = 1,
    Double = 2,
}

/// <summary>
/// BA-6: configuration for the optional payload lane attached to an
/// <see cref="AdjacencyBlockStoreV2"/>. <see cref="PropertyKeyId"/> identifies
/// which relationship property is inlined; <see cref="DefaultRaw"/> is the raw
/// 64-bit value substituted when an edge has no value for that key (or the
/// value has the wrong type). The default policy is fixed at view-build time
/// per codex_advice_3.md §7.2.
/// </summary>
public readonly struct PayloadLaneSpec
{
    public readonly PayloadKind Kind;
    public readonly int PropertyKeyId;
    public readonly long DefaultRaw;

    public PayloadLaneSpec(PayloadKind kind, int propertyKeyId, long defaultRaw)
    {
        Kind = kind;
        PropertyKeyId = propertyKeyId;
        DefaultRaw = defaultRaw;
    }

    public static PayloadLaneSpec ForInt64(int propertyKeyId, long defaultValue = 0)
        => new(PayloadKind.Int64, propertyKeyId, defaultValue);

    public static PayloadLaneSpec ForDouble(int propertyKeyId, double defaultValue = 0.0)
        => new(PayloadKind.Double, propertyKeyId, BitConverter.DoubleToInt64Bits(defaultValue));
}

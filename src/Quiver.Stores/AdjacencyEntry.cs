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

using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

/// <summary>
/// 物理 Sequence を transaction 境界の logical entity identity へ解決する。
/// </summary>
internal readonly struct EntityIdentityMaterializer(
    INodeStore nodes,
    IRelationshipStore relationships,
    IHyperedgeStore hyperedges)
{
    public bool TryNode(NodeId physical, out NodeId logical)
    {
        logical = NodeId.Invalid;
        if (!physical.IsValid) return false;
        int generation = nodes.CurrentGeneration(physical.Sequence);
        if (generation < 0) return false;
        var candidate = NodeId.Create(physical.Sequence, generation);
        using var read = nodes.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    public bool TryRelationship(RelationshipId physical, out RelationshipId logical)
    {
        logical = RelationshipId.Invalid;
        if (!physical.IsValid) return false;
        int generation = relationships.CurrentGeneration(physical.Sequence);
        if (generation < 0) return false;
        var candidate = RelationshipId.Create(physical.Sequence, generation);
        using var read = relationships.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    public bool TryHyperedge(HyperedgeId physical, out HyperedgeId logical)
    {
        logical = HyperedgeId.Invalid;
        if (!physical.IsValid) return false;
        int generation = hyperedges.CurrentGeneration(physical.Sequence);
        if (generation < 0) return false;
        var candidate = HyperedgeId.Create(physical.Sequence, generation);
        using var read = hyperedges.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }
}

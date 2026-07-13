using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

/// <summary>
/// 物理 Sequence を transaction 境界の logical entity identity へ解決する。
/// </summary>
internal readonly struct EntityIdentityMaterializer
{
    private readonly INodeStore _nodes;
    private readonly IRelationshipStore? _relationships;
    private readonly IHyperedgeStore? _hyperedges;

    public EntityIdentityMaterializer(
        INodeStore nodes,
        IRelationshipStore relationships,
        IHyperedgeStore hyperedges)
    {
        _nodes = nodes;
        _relationships = relationships;
        _hyperedges = hyperedges;
    }

    public EntityIdentityMaterializer(INodeStore nodes)
    {
        _nodes = nodes;
        _relationships = null;
        _hyperedges = null;
    }

    public bool TryNode(NodeId physical, out NodeId logical)
    {
        logical = NodeId.Invalid;
        if (!physical.IsValid) return false;
        int generation = _nodes.CurrentGeneration(physical.Sequence);
        if (generation < 0) return false;
        var candidate = NodeId.Create(physical.Sequence, generation);
        using var read = _nodes.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    public bool TryRelationship(RelationshipId physical, out RelationshipId logical)
    {
        logical = RelationshipId.Invalid;
        if (!physical.IsValid) return false;
        if (_relationships == null) return false;
        int generation = _relationships.CurrentGeneration(physical.Sequence);
        if (generation < 0) return false;
        var candidate = RelationshipId.Create(physical.Sequence, generation);
        using var read = _relationships.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    public bool TryHyperedge(HyperedgeId physical, out HyperedgeId logical)
    {
        logical = HyperedgeId.Invalid;
        if (!physical.IsValid) return false;
        if (_hyperedges == null) return false;
        int generation = _hyperedges.CurrentGeneration(physical.Sequence);
        if (generation < 0) return false;
        var candidate = HyperedgeId.Create(physical.Sequence, generation);
        using var read = _hyperedges.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }
}

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
        var candidate = Resolve(physical, _nodes.CurrentGeneration);
        if (!candidate.IsValid) return false;
        using var read = _nodes.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    /// <summary>
    /// 可視な primary owner-bound record が保持する node Sequence を full identity へ解決する。
    /// owner delete は参照 relationship/incidence と同じ logical delete 境界で行われ、
    /// reader horizon を越えるまで owner Sequence は再利用されないため、node 本体の再読は不要である。
    /// </summary>
    public bool TryNodeReferenceFromVisibleOwner(NodeId physical, out NodeId logical)
    {
        logical = NodeId.Invalid;
        if (!physical.IsValid) return false;

        int generation = _nodes.CurrentGeneration(physical.Sequence);
        if (generation < 0 || (physical.Generation != 0 && physical.Generation != generation))
            return false;

        logical = NodeId.Create(physical.Sequence, generation);
        return true;
    }

    public bool TryRelationship(RelationshipId physical, out RelationshipId logical)
    {
        logical = RelationshipId.Invalid;
        if (!physical.IsValid) return false;
        if (_relationships == null) return false;
        var candidate = Resolve(physical, _relationships.CurrentGeneration);
        if (!candidate.IsValid) return false;
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
        var candidate = Resolve(physical, _hyperedges.CurrentGeneration);
        if (!candidate.IsValid) return false;
        using var read = _hyperedges.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    // Generation 0 は store 内部の physical Sequence を表す入力に限る。
    // 世代付き入力を現世代へ置換すると stale identity が新しい slot 所有者を指すため、
    // primary Read にそのまま渡して reject させる。
    private static NodeId Resolve(NodeId id, Func<long, int> currentGeneration)
        => id.Generation != 0
            ? id
            : currentGeneration(id.Sequence) is var generation && generation >= 0
                ? NodeId.Create(id.Sequence, generation)
                : NodeId.Invalid;

    private static RelationshipId Resolve(RelationshipId id, Func<long, int> currentGeneration)
        => id.Generation != 0
            ? id
            : currentGeneration(id.Sequence) is var generation && generation >= 0
                ? RelationshipId.Create(id.Sequence, generation)
                : RelationshipId.Invalid;

    private static HyperedgeId Resolve(HyperedgeId id, Func<long, int> currentGeneration)
        => id.Generation != 0
            ? id
            : currentGeneration(id.Sequence) is var generation && generation >= 0
                ? HyperedgeId.Create(id.Sequence, generation)
                : HyperedgeId.Invalid;
}

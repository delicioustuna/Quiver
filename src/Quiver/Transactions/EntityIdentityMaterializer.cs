using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

/// <summary>
/// 物理 Sequence を transaction 境界の logical entity identity へ解決する。
/// </summary>
internal readonly struct EntityIdentityMaterializer
{
    private readonly IVertexStore _vertices;
    private readonly IEdgeStore? _edges;
    private readonly INexusStore? _nexuses;

    public EntityIdentityMaterializer(
        IVertexStore vertices,
        IEdgeStore edges,
        INexusStore nexuses)
    {
        _vertices = vertices;
        _edges = edges;
        _nexuses = nexuses;
    }

    public EntityIdentityMaterializer(IVertexStore vertices)
    {
        _vertices = vertices;
        _edges = null;
        _nexuses = null;
    }

    public bool TryVertex(VertexId physical, out VertexId logical)
    {
        logical = VertexId.Invalid;
        if (!physical.IsValid) return false;
        var candidate = Resolve(physical, _vertices.CurrentGeneration);
        if (!candidate.IsValid) return false;
        using var read = _vertices.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    /// <summary>
    /// 可視な primary owner-bound record が保持する vertex Sequence を full identity へ解決する。
    /// owner delete は参照 edge/incidence と同じ logical delete 境界で行われ、
    /// reader horizon を越えるまで owner Sequence は再利用されないため、vertex 本体の再読は不要である。
    /// </summary>
    public bool TryVertexReferenceFromVisibleOwner(VertexId physical, out VertexId logical)
    {
        logical = VertexId.Invalid;
        if (!physical.IsValid) return false;

        int generation = _vertices.CurrentGeneration(physical.Sequence);
        if (generation < 0 || (physical.Generation != 0 && physical.Generation != generation))
            return false;

        logical = VertexId.Create(physical.Sequence, generation);
        return true;
    }

    public bool TryEdge(EdgeId physical, out EdgeId logical)
    {
        logical = EdgeId.Invalid;
        if (!physical.IsValid) return false;
        if (_edges == null) return false;
        var candidate = Resolve(physical, _edges.CurrentGeneration);
        if (!candidate.IsValid) return false;
        using var read = _edges.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    public bool TryNexus(NexusId physical, out NexusId logical)
    {
        logical = NexusId.Invalid;
        if (!physical.IsValid) return false;
        if (_nexuses == null) return false;
        var candidate = Resolve(physical, _nexuses.CurrentGeneration);
        if (!candidate.IsValid) return false;
        using var read = _nexuses.Read(candidate);
        if (!read.InUse) return false;
        logical = read.Id;
        return true;
    }

    // Generation 0 は store 内部の physical Sequence を表す入力に限る。
    // 世代付き入力を現世代へ置換すると stale identity が新しい slot 所有者を指すため、
    // primary Read にそのまま渡して reject させる。
    private static VertexId Resolve(VertexId id, Func<long, int> currentGeneration)
        => id.Generation != 0
            ? id
            : currentGeneration(id.Sequence) is var generation && generation >= 0
                ? VertexId.Create(id.Sequence, generation)
                : VertexId.Invalid;

    private static EdgeId Resolve(EdgeId id, Func<long, int> currentGeneration)
        => id.Generation != 0
            ? id
            : currentGeneration(id.Sequence) is var generation && generation >= 0
                ? EdgeId.Create(id.Sequence, generation)
                : EdgeId.Invalid;

    private static NexusId Resolve(NexusId id, Func<long, int> currentGeneration)
        => id.Generation != 0
            ? id
            : currentGeneration(id.Sequence) is var generation && generation >= 0
                ? NexusId.Create(id.Sequence, generation)
                : NexusId.Invalid;
}

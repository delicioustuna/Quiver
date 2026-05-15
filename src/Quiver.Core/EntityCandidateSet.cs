namespace Quiver.Core;

/// <summary>
/// VEC-6: precomputed entity-id set used as the membership-probe side of
/// <c>IGraphAccessMethods.KnnSearchFiltered</c>. Lets graph-first plans hand
/// a candidate frontier (label / property / neighborhood matches) to the
/// vector access path so the vector index only scores the relevant nodes.
/// </summary>
/// <remarks>
/// Backed by a <see cref="HashSet{T}"/> for now. A bitmap variant in the
/// spirit of <c>FrontierSet</c> is a fair follow-up once dense node-id
/// patterns make it pay off — not needed for the initial filtered KNN
/// correctness path (codex_advice_3.md §6.4).
/// </remarks>
public sealed class EntityCandidateSet
{
    private readonly HashSet<long> _ids;

    public EntityCandidateSet(EntityKind kind, IEnumerable<long> ids)
    {
        Kind = kind;
        _ids = new HashSet<long>(ids);
    }

    public EntityKind Kind { get; }

    public int Count => _ids.Count;

    public bool Contains(long id) => _ids.Contains(id);

    public bool Contains(EntityKind kind, long id) => kind == Kind && _ids.Contains(id);
}

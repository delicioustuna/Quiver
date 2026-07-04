using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

internal sealed class NullHyperedgeStore : IHyperedgeStore
{
    public static readonly NullHyperedgeStore Instance = new();
    public long InUseCount => 0;

    public HyperedgeId Create(HyperedgeTypeId type, ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore incidenceStore, INodeIncidenceHeadStore nodeHeads)
        => throw new NotSupportedException("Hyperedge stores are not configured.");

    public void Delete(HyperedgeId hyperedgeId) { }
    public HyperedgeReadHandle Read(HyperedgeId hyperedgeId) => default;
    public HyperedgeWriteHandle Write(HyperedgeId hyperedgeId)
        => throw new NotSupportedException("Hyperedge stores are not configured.");
    public IEnumerable<HyperedgeId> Scan() => [];
}

internal sealed class NullIncidenceStore : IIncidenceStore
{
    public static readonly NullIncidenceStore Instance = new();
    public long InUseCount => 0;

    public IncidenceId Allocate(HyperedgeId hyperedgeId, NodeId nodeId, RoleId roleId,
        IncidenceId previousInNode, IncidenceId nextInNode, IncidenceId nextInHyperedge)
        => throw new NotSupportedException("Incidence stores are not configured.");

    public IncidenceReadHandle Read(IncidenceId incidenceId) => default;
    public IncidenceWriteHandle Write(IncidenceId incidenceId)
        => throw new NotSupportedException("Incidence stores are not configured.");

    public NodeIncidenceEnumerator EnumerateByNode(NodeId nodeId,
        INodeIncidenceHeadStore nodeHeads, IHyperedgeStore hyperedges)
        => new(this, hyperedges, IncidenceId.Invalid);

    public HyperedgeIncidenceEnumerator EnumerateByHyperedge(HyperedgeId hyperedgeId,
        IHyperedgeStore hyperedges)
        => new(this, hyperedges, hyperedgeId, IncidenceId.Invalid);
}

internal sealed class NullNodeIncidenceHeadStore : INodeIncidenceHeadStore
{
    public static readonly NullNodeIncidenceHeadStore Instance = new();
    public IncidenceId Get(NodeId nodeId) => IncidenceId.Invalid;
    public void Set(NodeId nodeId, IncidenceId incidenceId) { }
}

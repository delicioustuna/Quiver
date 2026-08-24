using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Transactions;

internal sealed class NullNexusStore : INexusStore
{
    public static readonly NullNexusStore Instance = new();
    public long InUseCount => 0;

    public NexusId Create(NexusTypeId type, ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore incidenceStore, IVertexIncidenceHeadStore vertexHeads)
        => throw new NotSupportedException("Nexus stores are not configured.");

    public void Delete(NexusId nexusId) { }
    public NexusReadHandle Read(NexusId nexusId) => default;
    public NexusWriteHandle Write(NexusId nexusId)
        => throw new NotSupportedException("Nexus stores are not configured.");
    public IEnumerable<NexusId> Scan() => [];

    public PropertyCursor EnumerateProperties(NexusId nexusId, IPropertyStore overflowStore)
        => new(overflowStore, default, PropertyVersionRef.Invalid);
}

internal sealed class NullIncidenceStore : IIncidenceStore
{
    public static readonly NullIncidenceStore Instance = new();
    public long InUseCount => 0;

    public IncidenceId Allocate(NexusId nexusId, VertexId vertexId, RoleId roleId,
        IncidenceId nextInVertex, IncidenceId nextInNexus)
        => throw new NotSupportedException("Incidence stores are not configured.");

    public IncidenceReadHandle Read(IncidenceId incidenceId) => default;
    public IncidenceWriteHandle Write(IncidenceId incidenceId)
        => throw new NotSupportedException("Incidence stores are not configured.");
    public void Free(IncidenceId incidenceId)
        => throw new NotSupportedException("Incidence stores are not configured.");

    public VertexIncidenceEnumerator EnumerateByVertex(VertexId vertexId,
        IVertexIncidenceHeadStore vertexHeads, INexusStore nexuses)
        => new(this, nexuses, IncidenceId.Invalid);

    public NexusIncidenceEnumerator EnumerateByNexus(NexusId nexusId,
        INexusStore nexuses)
        => new(this, nexuses, nexusId, IncidenceId.Invalid);
}

internal sealed class NullVertexIncidenceHeadStore : IVertexIncidenceHeadStore
{
    public static readonly NullVertexIncidenceHeadStore Instance = new();
    public IncidenceId Get(VertexId vertexId) => IncidenceId.Invalid;
    public void Set(VertexId vertexId, IncidenceId incidenceId) { }
}

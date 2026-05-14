using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

internal sealed class DiagnosticsApi : IDiagnosticsApi
{
    private readonly INodeStore _nodeStore;
    private readonly IRelationshipStore _relStore;
    private readonly IGraphAccessMethods _access;

    internal DiagnosticsApi(INodeStore nodeStore, IRelationshipStore relStore, IGraphAccessMethods access)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _access = access;
    }

    public DatabaseStatistics GetStatistics() => new(
        NodeCount: _nodeStore.InUseCount,
        RelationshipCount: _relStore.InUseCount,
        PropertyCount: 0,
        DataFileSize: 0,
        WalFileSize: 0,
        BufferPoolHits: 0,
        BufferPoolMisses: 0,
        AdjacencyFallbackCount: _access.AdjacencyFallbackCount);

    public ConsistencyReport CheckConsistency() => new(true, []);
}

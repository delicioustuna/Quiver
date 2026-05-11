using Quiver.Stores;

namespace Quiver;

internal sealed class DiagnosticsApi : IDiagnosticsApi
{
    private readonly INodeStore _nodeStore;
    private readonly IRelationshipStore _relStore;

    internal DiagnosticsApi(INodeStore nodeStore, IRelationshipStore relStore)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
    }

    public DatabaseStatistics GetStatistics() => new(
        NodeCount: _nodeStore.InUseCount,
        RelationshipCount: _relStore.InUseCount,
        PropertyCount: 0,
        DataFileSize: 0,
        WalFileSize: 0,
        BufferPoolHits: 0,
        BufferPoolMisses: 0);

    public ConsistencyReport CheckConsistency() => new(true, []);
}

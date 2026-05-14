namespace Quiver;

public interface IDiagnosticsApi
{
    DatabaseStatistics GetStatistics();
    ConsistencyReport CheckConsistency();
}

public sealed record DatabaseStatistics(
    long NodeCount,
    long RelationshipCount,
    long PropertyCount,
    long DataFileSize,
    long WalFileSize,
    long BufferPoolHits,
    long BufferPoolMisses,
    long AdjacencyFallbackCount);

public sealed record ConsistencyReport(
    bool IsConsistent,
    IReadOnlyList<string> Issues);

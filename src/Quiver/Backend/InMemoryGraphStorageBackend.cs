using Quiver.Core;
using Quiver.Maintenance;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// バイナリバックエンドと同じストア実装を RAM 上で提供するバックエンド。
/// スナップショット、永続化、vacuum はサポートしない。
/// </summary>
internal sealed class InMemoryGraphStorageBackend(
    BinaryGraphStorageBackend inner) : IGraphStorageBackendInternal
{
    private readonly BinaryGraphStorageBackend _inner = inner;

    public ISchemaApi Schema => _inner.Schema;
    public IDiagnosticsApi Diagnostics => _inner.Diagnostics;
    public IVectorStore Vectors => _inner.Vectors;
    public ITransactionManager Transactions => _inner.Transactions;
    public IGraphAccessMethods Access => _inner.Access;
    public BulkLoadCapabilities BulkLoad => _inner.BulkLoad;
    public string DataDirectory => string.Empty;

    public IGraphTransaction BeginGraphTransaction(IsolationLevel level, bool readOnly)
        => _inner.BeginGraphTransaction(level, readOnly);

    public void CreateSnapshot(string targetDirectory, SnapshotOptions? options = null)
        => throw new NotSupportedException(
            "インメモリバックエンドはスナップショットをサポートしていません。");

    public VacuumReport Vacuum(VacuumOptions? options = null)
        => new(
            ReclaimedNodes: 0,
            ReclaimedRelationships: 0,
            ReclaimedProperties: 0,
            PrunedCommittedTxEntries: 0,
            ElapsedMs: 0,
            HorizonTxId: 0,
            Skipped: true);

    public void Dispose()
        => _inner.Dispose();
}

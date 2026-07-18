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

    public IDiagnosticsApi Diagnostics => _inner.Diagnostics;
    public ITransactionManager Transactions => _inner.Transactions;
    public ISchemaCatalog SchemaCatalog => _inner.SchemaCatalog;
    internal SchemaApi SchemaApiForTesting => _inner.SchemaApiForTesting;
    public IGraphAccessMethods Access => _inner.Access;
    public BulkLoadCapabilities BulkLoad => _inner.BulkLoad;
    public string DataDirectory => string.Empty;

    public IReadTransaction BeginReadTransaction()
        => _inner.BeginReadTransaction();

    public IWriteTransaction BeginWriteTransaction()
        => _inner.BeginWriteTransaction();

    public void CreateSnapshot(string targetDirectory, SnapshotOptions? options = null)
        => throw new NotSupportedException(
            "インメモリバックエンドはスナップショットをサポートしていません。");

    public VacuumReport Vacuum(VacuumOptions? options = null)
        => new(
            ReclaimedVertices: 0,
            ReclaimedEdges: 0,
            ReclaimedProperties: 0,
            PrunedCommittedTxEntries: 0,
            ElapsedMs: 0,
            HorizonTxId: 0,
            Skipped: true);

    public void Dispose()
        => _inner.Dispose();
}

using Quiver.Transactions;

namespace Quiver.Storage.Sqlite;

/// <summary>
/// BA-5 MVP placeholder. The SQLite backend serves transactions through
/// <see cref="SqliteGraphStorageBackend.BeginGraphTransaction"/>; the legacy
/// <see cref="ITransaction"/> contract (with binary-specific
/// <c>INodeStore</c>/<c>IRelationshipStore</c> exposures) is not implemented here.
/// Callers like <c>GraphDatabase.CollectStats</c> that go through this path will
/// throw, by design — stats collection on the SQLite backend should run via
/// SQLite queries rather than walking the binary stores.
/// </summary>
internal sealed class SqliteTransactionManager : ITransactionManager
{
    private readonly SqliteGraphStorageBackend _backend;

    internal SqliteTransactionManager(SqliteGraphStorageBackend backend) => _backend = backend;

    public int ActiveCount => _backend.ActiveTransaction is null ? 0 : 1;
    public long OldestActiveLsn => 0;

    public ITransaction Begin(IsolationLevel level = IsolationLevel.SnapshotIsolation)
        => throw new NotSupportedException(
            "SQLite backend does not expose the binary ITransaction surface. " +
            "Use IGraphStorageBackend.BeginGraphTransaction(...) instead.");

    public void Dispose() { /* lifetime owned by SqliteGraphStorageBackend */ }
}

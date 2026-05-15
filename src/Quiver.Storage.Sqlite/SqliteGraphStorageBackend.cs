using Microsoft.Data.Sqlite;
using Quiver.Transactions;

namespace Quiver.Storage.Sqlite;

/// <summary>
/// SQLite-backed <see cref="IGraphStorageBackend"/>. Holds the single shared
/// <see cref="SqliteConnection"/>; schema / diagnostics queries either piggy-back
/// on the active <see cref="SqliteGraphTransaction"/> or run in autocommit when
/// no transaction is open.
/// </summary>
public sealed class SqliteGraphStorageBackend : IGraphStorageBackend
{
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private readonly SqliteSchemaApi _schema;
    private readonly SqliteDiagnosticsApi _diagnostics;
    private readonly SqliteGraphAccessMethods _access;
    private readonly SqliteTransactionManager _txManager;
    private readonly BulkLoadCapabilities _bulkLoad;

    // Only one outer transaction at a time (single-writer SQLite + sequential test model).
    private SqliteGraphTransaction? _active;

    internal SqliteGraphStorageBackend(SqliteConnection connection, string dbPath)
    {
        _connection = connection;
        _dbPath = dbPath;

        _schema = new SqliteSchemaApi(this);
        _diagnostics = new SqliteDiagnosticsApi(this);
        _access = new SqliteGraphAccessMethods();
        _txManager = new SqliteTransactionManager(this);

        // BA-5 MVP: no binary bulk loader; users wanting fast load should stay on
        // the binary backend.
        _bulkLoad = new BulkLoadCapabilities { BeginBinaryBulkLoad = null };
    }

    public ITransactionManager Transactions => _txManager;
    public ISchemaApi Schema => _schema;
    public IDiagnosticsApi Diagnostics => _diagnostics;
    public IGraphAccessMethods Access => _access;
    public BulkLoadCapabilities BulkLoad => _bulkLoad;

    internal SqliteConnection Connection => _connection;
    internal string DatabasePath => _dbPath;

    internal SqliteGraphTransaction? ActiveTransaction => _active;

    public IGraphTransaction BeginGraphTransaction(IsolationLevel level, bool readOnly)
    {
        if (_active is not null && _active.State == TransactionState.Active)
            throw new InvalidOperationException(
                "SQLite backend supports at most one active transaction; commit or rollback the previous one first.");

        // System.Data.IsolationLevel.Serializable maps to BEGIN IMMEDIATE in Microsoft.Data.Sqlite.
        var sqliteTx = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var tx = new SqliteGraphTransaction(this, sqliteTx, readOnly);
        _active = tx;
        return tx;
    }

    internal void OnTransactionFinished(SqliteGraphTransaction tx)
    {
        if (ReferenceEquals(_active, tx))
            _active = null;
    }

    public void Dispose()
    {
        _active?.Dispose();
        _active = null;
        _connection.Close();
        _connection.Dispose();
    }
}

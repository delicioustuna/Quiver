using Microsoft.Data.Sqlite;

namespace Quiver.Storage.Sqlite;

/// <summary>
/// Optional backend factory (BA-5). Inject via
/// <c>new GraphDatabaseOptions { BackendFactory = new SqliteGraphStorageBackendFactory() }</c>
/// when opening a database, or call <see cref="OpenBackend"/> directly. The
/// SQLite backend is a debug / durability backend, not a performance target —
/// hot paths still belong on the binary backend.
/// </summary>
public sealed class SqliteGraphStorageBackendFactory : IGraphStorageBackendFactory
{
    public IGraphStorageBackend Open(string directoryPath, GraphDatabaseOptions options)
        => OpenBackend(directoryPath, options);

    /// <summary>Resolved overload for callers that already know they want the SQLite backend.</summary>
    public static SqliteGraphStorageBackend OpenBackend(string directoryPath, GraphDatabaseOptions options)
    {
        Directory.CreateDirectory(directoryPath);
        var dbPath = Path.Combine(directoryPath, "quiver.sqlite");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        SqliteSchema.Bootstrap(connection);

        return new SqliteGraphStorageBackend(connection, dbPath);
    }
}

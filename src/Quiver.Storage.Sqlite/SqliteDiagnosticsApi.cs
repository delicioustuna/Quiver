namespace Quiver.Storage.Sqlite;

internal sealed class SqliteDiagnosticsApi : IDiagnosticsApi
{
    private readonly SqliteGraphStorageBackend _backend;

    internal SqliteDiagnosticsApi(SqliteGraphStorageBackend backend) => _backend = backend;

    public DatabaseStatistics GetStatistics()
    {
        long nodes = ScalarCount("SELECT COUNT(*) FROM nodes WHERE in_use = 1;");
        long rels  = ScalarCount("SELECT COUNT(*) FROM relationships WHERE in_use = 1;");
        long props = ScalarCount("SELECT COUNT(*) FROM node_properties;")
                   + ScalarCount("SELECT COUNT(*) FROM relationship_properties;");

        long fileSize = 0;
        try { fileSize = new FileInfo(_backend.DatabasePath).Length; }
        catch (IOException) { /* file may not be flushed yet */ }

        return new DatabaseStatistics(
            NodeCount: nodes,
            RelationshipCount: rels,
            PropertyCount: props,
            DataFileSize: fileSize,
            WalFileSize: 0,
            BufferPoolHits: 0,
            BufferPoolMisses: 0,
            AdjacencyFallbackCount: 0);
    }

    public ConsistencyReport CheckConsistency()
    {
        // PRAGMA integrity_check returns "ok" or a list of issues.
        using var cmd = _backend.Connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var issues = new List<string>();
        bool ok = true;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var line = reader.GetString(0);
            if (!string.Equals(line, "ok", StringComparison.Ordinal))
            {
                ok = false;
                issues.Add(line);
            }
        }
        return new ConsistencyReport(ok, issues);
    }

    private long ScalarCount(string sql)
    {
        using var cmd = _backend.Connection.CreateCommand();
        cmd.CommandText = sql;
        var active = _backend.ActiveTransaction?.SqliteTransaction;
        if (active is not null) cmd.Transaction = active;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }
}

using Microsoft.Data.Sqlite;

namespace Quiver.Storage.Sqlite;

/// <summary>
/// DDL bootstrap for the SQLite-backed Quiver storage engine (BA-5).
///
/// The schema favours external inspectability (DB Browser for SQLite, generic SQL
/// tooling) over raw throughput. property values live in type-discriminated columns
/// so debug queries like <c>SELECT * FROM node_properties WHERE text_value='Alice'</c>
/// work without decoding a binary blob.
/// </summary>
internal static class SqliteSchema
{
    internal const string CurrentVersion = "1";

    private const string Ddl = @"
CREATE TABLE IF NOT EXISTS quiver_meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS labels (
    id   INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS relationship_types (
    id   INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS property_keys (
    id   INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS nodes (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    label_id INTEGER NOT NULL,
    in_use   INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS idx_nodes_label ON nodes(label_id) WHERE in_use=1;

CREATE TABLE IF NOT EXISTS relationships (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    source_id INTEGER NOT NULL,
    target_id INTEGER NOT NULL,
    type_id   INTEGER NOT NULL,
    in_use    INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS idx_rel_source_type ON relationships(source_id, type_id) WHERE in_use=1;
CREATE INDEX IF NOT EXISTS idx_rel_target_type ON relationships(target_id, type_id) WHERE in_use=1;
CREATE INDEX IF NOT EXISTS idx_rel_type_source ON relationships(type_id, source_id) WHERE in_use=1;

CREATE TABLE IF NOT EXISTS node_properties (
    node_id      INTEGER NOT NULL,
    key_id       INTEGER NOT NULL,
    value_type   INTEGER NOT NULL,
    int_value    INTEGER,
    double_value REAL,
    text_value   TEXT,
    blob_value   BLOB,
    PRIMARY KEY (node_id, key_id)
);

CREATE INDEX IF NOT EXISTS idx_node_props_key_int  ON node_properties(key_id, int_value);
CREATE INDEX IF NOT EXISTS idx_node_props_key_text ON node_properties(key_id, text_value);

CREATE TABLE IF NOT EXISTS relationship_properties (
    relationship_id INTEGER NOT NULL,
    key_id          INTEGER NOT NULL,
    value_type      INTEGER NOT NULL,
    int_value       INTEGER,
    double_value    REAL,
    text_value      TEXT,
    blob_value      BLOB,
    PRIMARY KEY (relationship_id, key_id)
);

CREATE TABLE IF NOT EXISTS index_meta (
    name         TEXT PRIMARY KEY,
    value_type   INTEGER NOT NULL,
    label_name   TEXT,
    property_key TEXT,
    kind         INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS index_entries (
    name       TEXT    NOT NULL,
    value_type INTEGER NOT NULL,
    int_key    INTEGER,
    double_key REAL,
    text_key   TEXT,
    node_id    INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_index_entries_int  ON index_entries(name, int_key)    WHERE int_key    IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_index_entries_dbl  ON index_entries(name, double_key) WHERE double_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_index_entries_text ON index_entries(name, text_key)   WHERE text_key   IS NOT NULL;

-- VEC-2: vector index catalog + embedding task lifecycle. Payloads and ANN
-- structures live in a binary sidecar (codex_advice_3.md §6.5); SQLite only
-- holds the metadata callers need to inspect/manage indexes externally.
CREATE TABLE IF NOT EXISTS vector_indexes (
    name                   TEXT PRIMARY KEY,
    entity_kind            INTEGER NOT NULL,
    source_property_key_id INTEGER NOT NULL,
    dimensions             INTEGER NOT NULL,
    metric                 INTEGER NOT NULL,
    provider_id            TEXT NOT NULL,
    normalization_profile  TEXT,
    created_at_utc         TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS embedding_tasks (
    entity_kind         INTEGER NOT NULL,
    entity_id           INTEGER NOT NULL,
    index_name          TEXT NOT NULL,
    provider_id         TEXT NOT NULL,
    state               INTEGER NOT NULL,
    content_hash        TEXT,
    last_error          TEXT,
    last_updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (entity_kind, entity_id, index_name, provider_id)
);

CREATE INDEX IF NOT EXISTS idx_embedding_tasks_index ON embedding_tasks(index_name);
";

    /// <summary>
    /// Apply recommended PRAGMA and create tables if missing. Idempotent; the binary
    /// backend's <c>RecoveryManager</c> equivalent here is SQLite's own WAL.
    /// </summary>
    internal static void Bootstrap(SqliteConnection connection)
    {
        // Apply PRAGMA before DDL so the journal is set up in WAL mode from the start.
        Exec(connection, "PRAGMA journal_mode = WAL;");
        Exec(connection, "PRAGMA synchronous = NORMAL;");
        Exec(connection, "PRAGMA foreign_keys = ON;");
        Exec(connection, "PRAGMA temp_store = MEMORY;");
        Exec(connection, "PRAGMA busy_timeout = 5000;");

        Exec(connection, Ddl);

        // Stamp the schema version so future migrations have something to diff against.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO quiver_meta(key, value) VALUES ('schema_version', $v)
                            ON CONFLICT(key) DO NOTHING;";
        cmd.Parameters.AddWithValue("$v", CurrentVersion);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

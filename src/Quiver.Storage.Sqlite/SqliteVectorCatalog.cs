using Microsoft.Data.Sqlite;
using Quiver.Core;

namespace Quiver.Storage.Sqlite;

/// <summary>
/// SQLite-backed <see cref="IVectorCatalog"/>. Persists <see cref="VectorIndexSpec"/>
/// in <c>vector_indexes</c> and <see cref="EmbeddingTaskRecord"/> in
/// <c>embedding_tasks</c> (DDL bootstrapped by <see cref="SqliteSchema"/>).
/// Vector payloads / ANN structures stay out of SQLite so the table contents
/// remain inspectable with generic SQL tooling — see codex_advice_3.md §6.5.
/// VEC-2.
/// </summary>
public sealed class SqliteVectorCatalog : IVectorCatalog
{
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    /// <summary>
    /// 既にオープン済みの <see cref="SqliteConnection"/> をラップする。接続のライフタイムは
    /// 呼び出し側が所有し、カタログは Dispose しない。
    /// </summary>
    public SqliteVectorCatalog(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public void CreateIndex(VectorIndexSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrEmpty(spec.Name))
            throw new VectorException("Vector index name must not be empty.");
        if (spec.Dimensions <= 0)
            throw new VectorException(
                $"Vector index '{spec.Name}' must have positive dimensions (was {spec.Dimensions}).");

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO vector_indexes
                    (name, entity_kind, source_property_key_id, dimensions, metric,
                     provider_id, normalization_profile, created_at_utc)
                VALUES
                    ($name, $kind, $src, $dim, $metric, $provider, $norm, $created);";
            cmd.Parameters.AddWithValue("$name", spec.Name);
            cmd.Parameters.AddWithValue("$kind", (byte)spec.EntityKind);
            cmd.Parameters.AddWithValue("$src", spec.SourcePropertyKeyId.Value);
            cmd.Parameters.AddWithValue("$dim", spec.Dimensions);
            cmd.Parameters.AddWithValue("$metric", (byte)spec.Metric);
            cmd.Parameters.AddWithValue("$provider", spec.ProviderId);
            cmd.Parameters.AddWithValue("$norm",
                (object?)spec.NormalizationProfile ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created",
                DateTimeOffset.UtcNow.ToString("O"));

            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
            {
                throw new VectorException($"Vector index '{spec.Name}' already exists.", ex);
            }
        }
    }

    public bool DropIndex(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            // Cascade: drop dependent task rows first so we don't leave orphans.
            using (var tasks = _connection.CreateCommand())
            {
                tasks.CommandText = "DELETE FROM embedding_tasks WHERE index_name = $name;";
                tasks.Parameters.AddWithValue("$name", name);
                tasks.ExecuteNonQuery();
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM vector_indexes WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", name);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool TryGetIndex(string name, out VectorIndexSpec spec)
    {
        spec = null!;
        if (string.IsNullOrEmpty(name)) return false;

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT name, entity_kind, source_property_key_id, dimensions, metric,
                       provider_id, normalization_profile
                FROM   vector_indexes
                WHERE  name = $name;";
            cmd.Parameters.AddWithValue("$name", name);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return false;

            spec = ReadSpec(reader);
            return true;
        }
    }

    public IReadOnlyList<VectorIndexSpec> ListIndexes()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT name, entity_kind, source_property_key_id, dimensions, metric,
                       provider_id, normalization_profile
                FROM   vector_indexes
                ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            var list = new List<VectorIndexSpec>();
            while (reader.Read()) list.Add(ReadSpec(reader));
            return list;
        }
    }

    public EmbeddingTaskRecord? GetTask(EmbeddingTaskKey key)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT entity_kind, entity_id, index_name, provider_id, state,
                       content_hash, last_error, last_updated_at_utc
                FROM   embedding_tasks
                WHERE  entity_kind = $kind
                  AND  entity_id   = $id
                  AND  index_name  = $idx
                  AND  provider_id = $prov;";
            cmd.Parameters.AddWithValue("$kind", (byte)key.EntityKind);
            cmd.Parameters.AddWithValue("$id", key.EntityId);
            cmd.Parameters.AddWithValue("$idx", key.IndexName);
            cmd.Parameters.AddWithValue("$prov", key.ProviderId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTask(reader) : null;
        }
    }

    public void UpsertTask(EmbeddingTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO embedding_tasks
                    (entity_kind, entity_id, index_name, provider_id,
                     state, content_hash, last_error, last_updated_at_utc)
                VALUES
                    ($kind, $id, $idx, $prov, $state, $hash, $err, $upd)
                ON CONFLICT(entity_kind, entity_id, index_name, provider_id) DO UPDATE SET
                    state               = excluded.state,
                    content_hash        = excluded.content_hash,
                    last_error          = excluded.last_error,
                    last_updated_at_utc = excluded.last_updated_at_utc;";
            cmd.Parameters.AddWithValue("$kind", (byte)record.EntityKind);
            cmd.Parameters.AddWithValue("$id", record.EntityId);
            cmd.Parameters.AddWithValue("$idx", record.IndexName);
            cmd.Parameters.AddWithValue("$prov", record.ProviderId);
            cmd.Parameters.AddWithValue("$state", (byte)record.State);
            cmd.Parameters.AddWithValue("$hash", (object?)record.ContentHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)record.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$upd", record.LastUpdatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public bool DeleteTask(EmbeddingTaskKey key)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM embedding_tasks
                WHERE entity_kind = $kind
                  AND entity_id   = $id
                  AND index_name  = $idx
                  AND provider_id = $prov;";
            cmd.Parameters.AddWithValue("$kind", (byte)key.EntityKind);
            cmd.Parameters.AddWithValue("$id", key.EntityId);
            cmd.Parameters.AddWithValue("$idx", key.IndexName);
            cmd.Parameters.AddWithValue("$prov", key.ProviderId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IEnumerable<EmbeddingTaskRecord> ListTasks(string? indexName = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            if (indexName is null)
            {
                cmd.CommandText = @"
                    SELECT entity_kind, entity_id, index_name, provider_id, state,
                           content_hash, last_error, last_updated_at_utc
                    FROM   embedding_tasks;";
            }
            else
            {
                cmd.CommandText = @"
                    SELECT entity_kind, entity_id, index_name, provider_id, state,
                           content_hash, last_error, last_updated_at_utc
                    FROM   embedding_tasks
                    WHERE  index_name = $idx;";
                cmd.Parameters.AddWithValue("$idx", indexName);
            }

            using var reader = cmd.ExecuteReader();
            var list = new List<EmbeddingTaskRecord>();
            while (reader.Read()) list.Add(ReadTask(reader));
            return list;
        }
    }

    private static VectorIndexSpec ReadSpec(SqliteDataReader r) => new(
        Name: r.GetString(0),
        EntityKind: (EntityKind)r.GetByte(1),
        SourcePropertyKeyId: new PropertyKeyId(r.GetInt32(2)),
        Dimensions: r.GetInt32(3),
        Metric: (DistanceMetric)r.GetByte(4),
        ProviderId: r.GetString(5),
        NormalizationProfile: r.IsDBNull(6) ? null : r.GetString(6));

    private static EmbeddingTaskRecord ReadTask(SqliteDataReader r) => new(
        EntityKind: (EntityKind)r.GetByte(0),
        EntityId: r.GetInt64(1),
        IndexName: r.GetString(2),
        ProviderId: r.GetString(3),
        State: (EmbeddingTaskState)r.GetByte(4),
        ContentHash: r.IsDBNull(5) ? null : r.GetString(5),
        LastError: r.IsDBNull(6) ? null : r.GetString(6),
        LastUpdatedAtUtc: DateTimeOffset.Parse(r.GetString(7),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind));
}

using Microsoft.Data.Sqlite;
using Quiver.Core;

namespace Quiver.Storage.Sqlite;

internal sealed class SqliteSchemaApi : ISchemaApi
{
    private readonly SqliteGraphStorageBackend _backend;
    private readonly Dictionary<string, LabelId> _labelCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelationshipTypeId> _relTypeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyKeyId> _propKeyCache = new(StringComparer.Ordinal);

    internal SqliteSchemaApi(SqliteGraphStorageBackend backend) => _backend = backend;

    public LabelId GetOrCreateLabel(string name)
    {
        if (_labelCache.TryGetValue(name, out var cached)) return cached;
        var id = (int)GetOrCreateToken("labels", name);
        var token = new LabelId(id);
        _labelCache[name] = token;
        return token;
    }

    public RelationshipTypeId GetOrCreateRelationshipType(string name)
    {
        if (_relTypeCache.TryGetValue(name, out var cached)) return cached;
        var id = (int)GetOrCreateToken("relationship_types", name);
        var token = new RelationshipTypeId(id);
        _relTypeCache[name] = token;
        return token;
    }

    public PropertyKeyId GetOrCreatePropertyKey(string name)
    {
        if (_propKeyCache.TryGetValue(name, out var cached)) return cached;
        var id = (int)GetOrCreateToken("property_keys", name);
        var token = new PropertyKeyId(id);
        _propKeyCache[name] = token;
        return token;
    }

    internal bool TryGetLabel(string name, out LabelId id)
    {
        if (_labelCache.TryGetValue(name, out id)) return true;
        return TryGetToken("labels", name, out id, static i => new LabelId((int)i));
    }

    internal bool TryGetRelationshipType(string name, out RelationshipTypeId id)
    {
        if (_relTypeCache.TryGetValue(name, out id)) return true;
        return TryGetToken("relationship_types", name, out id, static i => new RelationshipTypeId((int)i));
    }

    internal bool TryGetPropertyKey(string name, out PropertyKeyId id)
    {
        if (_propKeyCache.TryGetValue(name, out id)) return true;
        return TryGetToken("property_keys", name, out id, static i => new PropertyKeyId((int)i));
    }

    public void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind)
    {
        // BA-5 MVP only models index metadata; entries land via tx.IndexInsert(...).
        // value_type is inferred from the IndexKind so we don't lose seek semantics.
        int valueType = kind switch
        {
            IndexKind.Int32Equality or IndexKind.Int64Equality => (int)Stores.PropertyValueType.Int64,
            IndexKind.DoubleEquality => (int)Stores.PropertyValueType.Double,
            IndexKind.StringEquality or IndexKind.StringRange => (int)Stores.PropertyValueType.String,
            _ => 0,
        };

        Execute(@"INSERT INTO index_meta(name, value_type, label_name, property_key, kind)
                   VALUES ($n, $vt, $lbl, $pk, $k)
                   ON CONFLICT(name) DO UPDATE SET
                       value_type   = excluded.value_type,
                       label_name   = excluded.label_name,
                       property_key = excluded.property_key,
                       kind         = excluded.kind;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", indexName);
                cmd.Parameters.AddWithValue("$vt", valueType);
                cmd.Parameters.AddWithValue("$lbl", label);
                cmd.Parameters.AddWithValue("$pk", propertyKey);
                cmd.Parameters.AddWithValue("$k", (int)kind);
            });
    }

    public void DropIndex(string indexName)
    {
        Execute("DELETE FROM index_meta    WHERE name = $n;", c => c.Parameters.AddWithValue("$n", indexName));
        Execute("DELETE FROM index_entries WHERE name = $n;", c => c.Parameters.AddWithValue("$n", indexName));
    }

    public IReadOnlyList<IndexInfo> ListIndexes()
    {
        var list = new List<IndexInfo>();
        using var cmd = NewCommand();
        cmd.CommandText = @"SELECT name, COALESCE(label_name,''), COALESCE(property_key,''), kind,
                                   (SELECT COUNT(*) FROM index_entries WHERE index_entries.name = index_meta.name)
                            FROM index_meta;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new IndexInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                (IndexKind)reader.GetInt32(3),
                reader.GetInt64(4)));
        }
        return list;
    }

    // ---------------- shared helpers ----------------

    private long GetOrCreateToken(string table, string name)
    {
        // SELECT first to keep autoincrement IDs from being burned on conflict.
        using (var sel = NewCommand())
        {
            sel.CommandText = $"SELECT id FROM {table} WHERE name = $n;";
            sel.Parameters.AddWithValue("$n", name);
            var existing = sel.ExecuteScalar();
            if (existing is not null && existing is not DBNull)
                return Convert.ToInt64(existing);
        }

        using var ins = NewCommand();
        ins.CommandText = $"INSERT INTO {table}(name) VALUES ($n) RETURNING id;";
        ins.Parameters.AddWithValue("$n", name);
        return (long)ins.ExecuteScalar()!;
    }

    private bool TryGetToken<TToken>(string table, string name, out TToken id, Func<long, TToken> make)
        where TToken : struct
    {
        using var cmd = NewCommand();
        cmd.CommandText = $"SELECT id FROM {table} WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) { id = default; return false; }
        id = make(Convert.ToInt64(result));
        return true;
    }

    private void Execute(string sql, Action<SqliteCommand> configure)
    {
        using var cmd = NewCommand();
        cmd.CommandText = sql;
        configure(cmd);
        cmd.ExecuteNonQuery();
    }

    internal SqliteCommand NewCommand()
    {
        var cmd = _backend.Connection.CreateCommand();
        var active = _backend.ActiveTransaction?.SqliteTransaction;
        if (active is not null) cmd.Transaction = active;
        return cmd;
    }
}

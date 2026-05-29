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

    public string? GetLabelName(LabelId id)
    {
        if (!id.IsValid) return null;
        using var cmd = NewCommand();
        cmd.CommandText = "SELECT name FROM labels WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.Value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : (string)result;
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

    internal bool TryGetLabel(string name, out LabelId id) => TryGetLabelId(name, out id);
    internal bool TryGetRelationshipType(string name, out RelationshipTypeId id) => TryGetRelationshipTypeId(name, out id);
    internal bool TryGetPropertyKey(string name, out PropertyKeyId id) => TryGetPropertyKeyId(name, out id);

    public bool TryGetLabelId(string name, out LabelId id)
    {
        if (_labelCache.TryGetValue(name, out id)) return true;
        return TryGetToken("labels", name, out id, static i => new LabelId((int)i));
    }

    public bool TryGetRelationshipTypeId(string name, out RelationshipTypeId id)
    {
        if (_relTypeCache.TryGetValue(name, out id)) return true;
        return TryGetToken("relationship_types", name, out id, static i => new RelationshipTypeId((int)i));
    }

    public bool TryGetPropertyKeyId(string name, out PropertyKeyId id)
    {
        if (_propKeyCache.TryGetValue(name, out id)) return true;
        return TryGetToken("property_keys", name, out id, static i => new PropertyKeyId((int)i));
    }

    public bool IndexExists(string indexName)
    {
        using var cmd = NewCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM index_meta WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", indexName);
        return Convert.ToInt64(cmd.ExecuteScalar()!) > 0;
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

    public bool RenameLabel(string oldName, string newName) => RenameToken("labels", oldName, newName, _labelCache);
    public bool RenamePropertyKey(string oldName, string newName) => RenameToken("property_keys", oldName, newName, _propKeyCache);
    public bool RenameRelationshipType(string oldName, string newName) => RenameToken("relationship_types", oldName, newName, _relTypeCache);

    public bool RenameIndex(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (oldName == newName) return false;

        // 既存メタの存在チェック (旧 / 新)。
        using (var sel = NewCommand())
        {
            sel.CommandText = "SELECT COUNT(*) FROM index_meta WHERE name = $n;";
            sel.Parameters.AddWithValue("$n", oldName);
            var oldExists = Convert.ToInt64(sel.ExecuteScalar()!) > 0;
            if (!oldExists)
            {
                sel.Parameters.Clear();
                sel.Parameters.AddWithValue("$n", newName);
                var newExists = Convert.ToInt64(sel.ExecuteScalar()!) > 0;
                return newExists;
            }
        }
        using (var chk = NewCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM index_meta WHERE name = $n;";
            chk.Parameters.AddWithValue("$n", newName);
            if (Convert.ToInt64(chk.ExecuteScalar()!) > 0)
                throw new InvalidOperationException($"Index '{newName}' already exists.");
        }
        Execute("UPDATE index_meta    SET name = $new WHERE name = $old;",
            c => { c.Parameters.AddWithValue("$new", newName); c.Parameters.AddWithValue("$old", oldName); });
        Execute("UPDATE index_entries SET name = $new WHERE name = $old;",
            c => { c.Parameters.AddWithValue("$new", newName); c.Parameters.AddWithValue("$old", oldName); });
        return true;
    }

    private bool RenameToken<TToken>(
        string table, string oldName, string newName, Dictionary<string, TToken> cache)
        where TToken : struct
    {
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (oldName == newName) return false;

        // 旧名の存在 & 新名衝突チェック。
        long? oldId = null;
        using (var sel = NewCommand())
        {
            sel.CommandText = $"SELECT id FROM {table} WHERE name = $n;";
            sel.Parameters.AddWithValue("$n", oldName);
            var r = sel.ExecuteScalar();
            if (r is not null && r is not DBNull) oldId = Convert.ToInt64(r);
        }
        if (!oldId.HasValue)
        {
            // OP-4 (fix A): 冪等 no-op パスでも cache を両方クリアして再 fetch を強制する。
            // rollback 後に旧 rename の cache 残骸が残るのを防ぐ。
            cache.Remove(oldName);
            cache.Remove(newName);
            using var chk = NewCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM {table} WHERE name = $n;";
            chk.Parameters.AddWithValue("$n", newName);
            return Convert.ToInt64(chk.ExecuteScalar()!) > 0;
        }
        using (var chk = NewCommand())
        {
            chk.CommandText = $"SELECT COUNT(*) FROM {table} WHERE name = $n;";
            chk.Parameters.AddWithValue("$n", newName);
            if (Convert.ToInt64(chk.ExecuteScalar()!) > 0)
                throw new InvalidOperationException($"Token '{newName}' already exists in {table}.");
        }
        Execute($"UPDATE {table} SET name = $new WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$new", newName);
            c.Parameters.AddWithValue("$id", oldId.Value);
        });
        // OP-4 (fix A): commit / rollback どちらでも DB 真実が再 fetch されるよう、両方をクリア。
        // rollback 時に MigrationContext の undo で再 SELECT されると DB は旧名に戻っているため、
        // cache に "Person" が残っているとそれが LabelId(1) を返し、GetLabelName(1) は "User" を返す
        // 不整合になる。両方クリアして commit 後 / rollback 後どちらも DB 真実から refetch させる。
        cache.Remove(oldName);
        cache.Remove(newName);
        return true;
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

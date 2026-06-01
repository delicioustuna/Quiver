using System.Text;
using Microsoft.Data.Sqlite;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Storage.Sqlite;

/// <summary>
/// SQLite-backed <see cref="IGraphTransaction"/> implementation (BA-5 MVP).
///
/// <para>
/// We bypass the binary <c>ITransaction</c> / store interfaces entirely — those
/// are page-layout-bound — and execute graph mutations directly against the
/// SQLite tables defined in <see cref="SqliteSchema"/>. Durability is delegated
/// to SQLite's own WAL via <c>BEGIN IMMEDIATE</c> / <c>COMMIT</c> / <c>ROLLBACK</c>.
/// </para>
/// <para>
/// Relationship enumeration is rebuilt eagerly per call via a temporary
/// <see cref="SqliteRelChainStore"/> so the public <see cref="RelationshipEnumerator"/>
/// ref-struct contract is honoured without persisting linked-list pointers.
/// </para>
/// </summary>
internal sealed class SqliteGraphTransaction : IGraphTransactionInternal
{
    private static long _txIdCounter;

    private readonly SqliteGraphStorageBackend _backend;
    private readonly SqliteTransaction _sqliteTx;
    private readonly bool _isReadOnly;
    private List<Action>? _onCommitted;
    private List<Action>? _onRolledBack;
    private TransactionState _state;

    internal SqliteGraphTransaction(
        SqliteGraphStorageBackend backend,
        SqliteTransaction sqliteTx,
        bool isReadOnly)
    {
        _backend = backend;
        _sqliteTx = sqliteTx;
        _isReadOnly = isReadOnly;
        _state = TransactionState.Active;
        Id = new TransactionId(System.Threading.Interlocked.Increment(ref _txIdCounter));
    }

    internal SqliteTransaction SqliteTransaction => _sqliteTx;

    public TransactionId Id { get; }
    public TransactionState State => _state;
    public bool IsReadOnly => _isReadOnly;
    public IAdjacencyBlockStore? AdjacencyBlocks => null;

    /// <summary>
    /// VEC-12: backend の access methods。SQLite backend は <c>LabelNodeIndex</c> sidecar を
    /// 持たないため <see cref="IGraphAccessMethods.HasFastLabelIndex"/> は <c>false</c> で、
    /// PendingKnnBuilder は legacy 30% 単一閾値経路にフォールバックする。
    /// </summary>
    public IGraphAccessMethods Access => _backend.Access;

    // ============================================================
    // Nodes
    // ============================================================

    public NodeId CreateNode(string label)
        => CreateNode(((SqliteSchemaApi)_backend.Schema).GetOrCreateLabel(label));

    public NodeId CreateNode(LabelId labelId)
    {
        EnsureWritable();
        using var cmd = NewCommand();
        cmd.CommandText = "INSERT INTO nodes(label_id, in_use) VALUES ($lbl, 1) RETURNING id;";
        cmd.Parameters.AddWithValue("$lbl", labelId.Value);
        var id = (long)cmd.ExecuteScalar()!;
        return new NodeId(id);
    }

    public void DeleteNode(NodeId nodeId)
    {
        EnsureWritable();

        // バイナリバックエンドと同じ挙動: ノードを tombstone する前に、接続している
        // リレーションシップ (とそのプロパティ) をカスケード削除する。
        var incident = new List<long>();
        using (var cmd = NewCommand())
        {
            cmd.CommandText = "SELECT id FROM relationships WHERE (source_id = $n OR target_id = $n) AND in_use = 1;";
            cmd.Parameters.AddWithValue("$n", nodeId.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) incident.Add(reader.GetInt64(0));
        }
        foreach (var relId in incident)
            DeleteRelationship(new RelationshipId(relId));

        using var del = NewCommand();
        del.CommandText = "DELETE FROM node_properties WHERE node_id = $n;" +
                          "UPDATE nodes SET in_use = 0 WHERE id = $n;";
        del.Parameters.AddWithValue("$n", nodeId.Value);
        del.ExecuteNonQuery();
    }

    public bool NodeExists(NodeId nodeId)
    {
        using var cmd = NewCommand();
        cmd.CommandText = "SELECT in_use FROM nodes WHERE id = $n;";
        cmd.Parameters.AddWithValue("$n", nodeId.Value);
        var v = cmd.ExecuteScalar();
        return v is not null and not DBNull && Convert.ToInt64(v) == 1L;
    }

    public (NodeId Id, bool Created) MergeNode(string label, string matchKey, in PropertyValue matchValue)
    {
        EnsureWritable();
        var schemaApi = (SqliteSchemaApi)_backend.Schema;
        var labelId = schemaApi.GetOrCreateLabel(label);

        if (schemaApi.TryGetPropertyKey(matchKey, out var keyId))
        {
            var existing = FindNodeByLabelAndProperty(labelId.Value, keyId.Value, in matchValue);
            if (existing.HasValue) return (new NodeId(existing.Value), false);
        }

        var newId = CreateNode(labelId);
        SetProperty(newId, matchKey, in matchValue);
        return (newId, true);
    }

    private long? FindNodeByLabelAndProperty(int labelId, int keyId, in PropertyValue value)
    {
        string typeClause;
        object paramValue;
        switch (value.Type)
        {
            case PropertyValueType.Bool:
            case PropertyValueType.Int32:
            case PropertyValueType.Int64:
                typeClause = "p.value_type IN (1,2,3) AND p.int_value = $v";
                paramValue = value.Int64Value;
                break;
            case PropertyValueType.Double:
                typeClause = "p.value_type = 4 AND p.double_value = $v";
                paramValue = value.DoubleValue;
                break;
            case PropertyValueType.String:
                typeClause = "p.value_type = 5 AND p.text_value = $v";
                paramValue = Encoding.UTF8.GetString(value.Utf8StringValue);
                break;
            case PropertyValueType.Bytes:
                typeClause = "p.value_type = 6 AND p.blob_value = $v";
                paramValue = value.BytesValue.ToArray();
                break;
            default:
                return null;
        }

        using var cmd = NewCommand();
        cmd.CommandText = $@"SELECT n.id FROM nodes n
                             JOIN node_properties p ON p.node_id = n.id
                             WHERE n.label_id = $lbl AND n.in_use = 1
                               AND p.key_id = $k AND {typeClause}
                             ORDER BY n.id LIMIT 1;";
        cmd.Parameters.AddWithValue("$lbl", labelId);
        cmd.Parameters.AddWithValue("$k",   keyId);
        cmd.Parameters.AddWithValue("$v",   paramValue);
        var r = cmd.ExecuteScalar();
        if (r is null or DBNull) return null;
        return Convert.ToInt64(r);
    }

    // ============================================================
    // Relationships
    // ============================================================

    public RelationshipId CreateRelationship(NodeId source, NodeId target, string type)
        => CreateRelationship(source, target,
            ((SqliteSchemaApi)_backend.Schema).GetOrCreateRelationshipType(type));

    public RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId)
    {
        EnsureWritable();
        using var cmd = NewCommand();
        cmd.CommandText = @"INSERT INTO relationships(source_id, target_id, type_id, in_use)
                            VALUES ($s, $t, $ty, 1) RETURNING id;";
        cmd.Parameters.AddWithValue("$s",  source.Value);
        cmd.Parameters.AddWithValue("$t",  target.Value);
        cmd.Parameters.AddWithValue("$ty", typeId.Value);
        var id = (long)cmd.ExecuteScalar()!;
        return new RelationshipId(id);
    }

    public void DeleteRelationship(RelationshipId relId)
    {
        EnsureWritable();
        using var cmd = NewCommand();
        cmd.CommandText = @"DELETE FROM relationship_properties WHERE relationship_id = $r;
                            UPDATE relationships SET in_use = 0 WHERE id = $r;";
        cmd.Parameters.AddWithValue("$r", relId.Value);
        cmd.ExecuteNonQuery();
    }

    public RelationshipEnumerator EnumerateRelationships(
        NodeId nodeId,
        Direction direction = Direction.Both,
        string? typeFilter = null)
    {
        var rows = new List<SqliteRelChainStore.RelRow>();
        var schemaApi = (SqliteSchemaApi)_backend.Schema;

        // Type filter is optional and may reference an as-yet-unknown name;
        // an unknown type just collapses the result to empty rather than
        // erroring out, mirroring the binary backend semantics.
        int? filterTypeId = null;
        if (typeFilter is not null)
        {
            if (!schemaApi.TryGetRelationshipType(typeFilter, out var t))
            {
                var empty = new SqliteRelChainStore(nodeId, rows);
                return new RelationshipEnumerator(empty, nodeId, RelationshipId.Invalid);
            }
            filterTypeId = t.Value;
        }

        using var cmd = NewCommand();
        if (filterTypeId is null)
        {
            cmd.CommandText = @"SELECT id, source_id, target_id, type_id
                                FROM relationships
                                WHERE (source_id = $n OR target_id = $n) AND in_use = 1
                                ORDER BY id;";
            cmd.Parameters.AddWithValue("$n", nodeId.Value);
        }
        else
        {
            cmd.CommandText = @"SELECT id, source_id, target_id, type_id
                                FROM relationships
                                WHERE (source_id = $n OR target_id = $n)
                                  AND type_id = $ty
                                  AND in_use  = 1
                                ORDER BY id;";
            cmd.Parameters.AddWithValue("$n",  nodeId.Value);
            cmd.Parameters.AddWithValue("$ty", filterTypeId.Value);
        }
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new SqliteRelChainStore.RelRow(
                    Id: reader.GetInt64(0),
                    Source: reader.GetInt64(1),
                    Target: reader.GetInt64(2),
                    TypeId: reader.GetInt32(3)));
            }
        }

        var chain = new SqliteRelChainStore(nodeId, rows);
        if (filterTypeId is null)
            return new RelationshipEnumerator(chain, nodeId, chain.FirstId);

        // The ref-struct enumerator filters by type+direction internally, so
        // we just hand it the pre-narrowed chain.
        return new RelationshipEnumerator(
            chain, nodeId, chain.FirstId, new RelationshipTypeId(filterTypeId.Value), direction);
    }

    // ============================================================
    // Properties
    // ============================================================

    public void SetProperty(NodeId nodeId, string key, in PropertyValue value)
    {
        EnsureWritable();
        var keyId = ((SqliteSchemaApi)_backend.Schema).GetOrCreatePropertyKey(key);
        WriteProperty("node_properties", "node_id", nodeId.Value, keyId.Value, in value);
    }

    public void SetProperty(RelationshipId relId, string key, in PropertyValue value)
    {
        EnsureWritable();
        var keyId = ((SqliteSchemaApi)_backend.Schema).GetOrCreatePropertyKey(key);
        WriteProperty("relationship_properties", "relationship_id", relId.Value, keyId.Value, in value);
    }

    public void RemoveProperty(NodeId nodeId, string key)
    {
        EnsureWritable();
        if (!((SqliteSchemaApi)_backend.Schema).TryGetPropertyKey(key, out var keyId)) return;
        using var cmd = NewCommand();
        cmd.CommandText = "DELETE FROM node_properties WHERE node_id = $n AND key_id = $k;";
        cmd.Parameters.AddWithValue("$n", nodeId.Value);
        cmd.Parameters.AddWithValue("$k", keyId.Value);
        cmd.ExecuteNonQuery();
    }

    public PropertyValue GetProperty(NodeId nodeId, string key)
    {
        if (!((SqliteSchemaApi)_backend.Schema).TryGetPropertyKey(key, out var keyId))
            return default;
        return ReadProperty("node_properties", "node_id", nodeId.Value, keyId.Value);
    }

    public PropertyValue GetProperty(RelationshipId relId, string key)
    {
        if (!((SqliteSchemaApi)_backend.Schema).TryGetPropertyKey(key, out var keyId))
            return default;
        return ReadProperty("relationship_properties", "relationship_id", relId.Value, keyId.Value);
    }

    public bool HasProperty(NodeId nodeId, string key)
    {
        if (!((SqliteSchemaApi)_backend.Schema).TryGetPropertyKey(key, out var keyId))
            return false;
        using var cmd = NewCommand();
        cmd.CommandText = "SELECT 1 FROM node_properties WHERE node_id = $n AND key_id = $k;";
        cmd.Parameters.AddWithValue("$n", nodeId.Value);
        cmd.Parameters.AddWithValue("$k", keyId.Value);
        return cmd.ExecuteScalar() is not null;
    }

    public PropertyEnumerator EnumerateProperties(NodeId nodeId)
        => throw new NotSupportedException(
            "SQLite backend (BA-5 MVP) does not implement chained PropertyEnumerator. " +
            "Iterate via SQL or extend the backend.");

    // ============================================================
    // Indexes
    // ============================================================

    public void IndexInsert(string indexName, string keyValue, NodeId nodeId)
    {
        EnsureWritable();
        EnsureIndexMeta(indexName, (int)PropertyValueType.String);
        using var cmd = NewCommand();
        cmd.CommandText = @"INSERT INTO index_entries(name, value_type, text_key, node_id)
                            VALUES ($n, $vt, $k, $nid);";
        cmd.Parameters.AddWithValue("$n",   indexName);
        cmd.Parameters.AddWithValue("$vt",  (int)PropertyValueType.String);
        cmd.Parameters.AddWithValue("$k",   keyValue);
        cmd.Parameters.AddWithValue("$nid", nodeId.Value);
        cmd.ExecuteNonQuery();
    }

    public void IndexInsert(string indexName, long keyValue, NodeId nodeId)
    {
        EnsureWritable();
        EnsureIndexMeta(indexName, (int)PropertyValueType.Int64);
        using var cmd = NewCommand();
        cmd.CommandText = @"INSERT INTO index_entries(name, value_type, int_key, node_id)
                            VALUES ($n, $vt, $k, $nid);";
        cmd.Parameters.AddWithValue("$n",   indexName);
        cmd.Parameters.AddWithValue("$vt",  (int)PropertyValueType.Int64);
        cmd.Parameters.AddWithValue("$k",   keyValue);
        cmd.Parameters.AddWithValue("$nid", nodeId.Value);
        cmd.ExecuteNonQuery();
    }

    public void IndexInsert(string indexName, double keyValue, NodeId nodeId)
    {
        EnsureWritable();
        EnsureIndexMeta(indexName, (int)PropertyValueType.Double);
        using var cmd = NewCommand();
        cmd.CommandText = @"INSERT INTO index_entries(name, value_type, double_key, node_id)
                            VALUES ($n, $vt, $k, $nid);";
        cmd.Parameters.AddWithValue("$n",   indexName);
        cmd.Parameters.AddWithValue("$vt",  (int)PropertyValueType.Double);
        cmd.Parameters.AddWithValue("$k",   keyValue);
        cmd.Parameters.AddWithValue("$nid", nodeId.Value);
        cmd.ExecuteNonQuery();
    }

    public NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key)
    {
        // The contract test asserts that seeks against unknown indexes return
        // an empty enumerator rather than throwing — keep that semantics here.
        var results = new List<long>();
        switch (key.Type)
        {
            case PropertyValueType.Int32:
            case PropertyValueType.Int64:
            case PropertyValueType.Bool:
                SeekInto(results, indexName, "int_key", key.Int64Value);
                break;
            case PropertyValueType.Double:
                SeekInto(results, indexName, "double_key", key.DoubleValue);
                break;
            case PropertyValueType.String:
                SeekInto(results, indexName, "text_key", Encoding.UTF8.GetString(key.Utf8StringValue));
                break;
        }
        return new NodeIdEnumerator(results);
    }

    public NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to,   bool toInclusive)
        => throw new NotSupportedException(
            "SQLite backend (BA-5 MVP) does not implement RangeIndex; add via SQL BETWEEN if needed.");

    // ============================================================
    // Query plan / cursor — not part of BA-5 MVP
    // ============================================================

    public QueryResult Execute(IPhysicalOperator plan)
        => throw new NotSupportedException(
            "SQLite backend does not run physical operators (BA-5 MVP). " +
            "Open a binary-backend GraphDatabase to use IPhysicalOperator pipelines.");

    public IQueryCursor ExecuteCursor(IPhysicalOperator plan)
        => throw new NotSupportedException(
            "SQLite backend does not run physical operators (BA-5 MVP).");

    // ============================================================
    // Lifecycle
    // ============================================================

    public void Commit()
    {
        if (_state != TransactionState.Active)
            throw new InvalidOperationException($"Cannot commit a transaction in state {_state}.");
        _state = TransactionState.Preparing;
        try
        {
            _sqliteTx.Commit();
            _state = TransactionState.Committed;
            _backend.OnTransactionFinished(this);
        }
        catch
        {
            _state = TransactionState.Aborted;
            _backend.OnTransactionFinished(this);
            FireHooks(_onRolledBack);
            throw;
        }
        FireHooks(_onCommitted);
    }

    public void Rollback()
    {
        if (_state != TransactionState.Active) return;
        _sqliteTx.Rollback();
        _state = TransactionState.Aborted;
        _backend.OnTransactionFinished(this);
        FireHooks(_onRolledBack);
    }

    public void Dispose()
    {
        if (_state == TransactionState.Active)
        {
            try { _sqliteTx.Rollback(); }
            catch (SqliteException) { /* connection already closed */ }
            _state = TransactionState.Aborted;
            _backend.OnTransactionFinished(this);
            FireHooks(_onRolledBack);
        }
        _sqliteTx.Dispose();
    }

    // ============================================================
    // FT-23: Savepoint / nested undo
    // SQLite はネイティブに SAVEPOINT / ROLLBACK TO / RELEASE をサポートしているので
    // それをそのままラップする。SQLite の SAVEPOINT 名は識別子なので、Quiver の
    // 連番 + 任意名を組み合わせて衝突しないラベルを生成する。
    // ============================================================

    private long _nextSavepointId;
    private List<(long Id, string SqliteName)>? _savepoints;

    public SavepointId Savepoint(string? name = null)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot create savepoint: transaction is not Active.");
        long id = ++_nextSavepointId;
        // SQLite identifier として安全な名前を作る (英数字 + アンダースコアのみ)。
        string sqliteName = $"quiver_sp_{id}";
        using (var cmd = _sqliteTx.Connection!.CreateCommand())
        {
            cmd.Transaction = _sqliteTx;
            cmd.CommandText = $"SAVEPOINT {sqliteName};";
            cmd.ExecuteNonQuery();
        }
        (_savepoints ??= new List<(long, string)>()).Add((id, sqliteName));
        return new SavepointId(id, name);
    }

    public void RollbackTo(SavepointId savepoint)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot rollback to savepoint: transaction is not Active.");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");
        var (_, sqliteName) = _savepoints![index];
        // SQL 標準: ROLLBACK TO Sn は Sn より新しい全ての savepoint も解放する。Sn 自身は残す。
        _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);
        using var cmd = _sqliteTx.Connection!.CreateCommand();
        cmd.Transaction = _sqliteTx;
        cmd.CommandText = $"ROLLBACK TO SAVEPOINT {sqliteName};";
        cmd.ExecuteNonQuery();
    }

    public void ReleaseSavepoint(SavepointId savepoint)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot release savepoint: transaction is not Active.");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");
        var (_, sqliteName) = _savepoints![index];
        _savepoints.RemoveRange(index, _savepoints.Count - index);
        using var cmd = _sqliteTx.Connection!.CreateCommand();
        cmd.Transaction = _sqliteTx;
        cmd.CommandText = $"RELEASE SAVEPOINT {sqliteName};";
        cmd.ExecuteNonQuery();
    }

    private int FindSavepointIndex(long id)
    {
        if (_savepoints == null) return -1;
        for (int i = 0; i < _savepoints.Count; i++)
        {
            if (_savepoints[i].Id == id) return i;
        }
        return -1;
    }

    // VEC-3: post-commit / post-rollback hooks. Semantics mirror the binary
    // backend in Quiver.Transactions.Transaction.
    public void OnCommitted(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        switch (_state)
        {
            case TransactionState.Committed:
                SafeInvoke(callback);
                return;
            case TransactionState.Aborted:
                return;
            default:
                (_onCommitted ??= new List<Action>()).Add(callback);
                return;
        }
    }

    public void OnRolledBack(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        switch (_state)
        {
            case TransactionState.Aborted:
                SafeInvoke(callback);
                return;
            case TransactionState.Committed:
                return;
            default:
                (_onRolledBack ??= new List<Action>()).Add(callback);
                return;
        }
    }

    private static void FireHooks(List<Action>? hooks)
    {
        if (hooks == null) return;
        for (int i = 0; i < hooks.Count; i++) SafeInvoke(hooks[i]);
    }

    private static void SafeInvoke(Action callback)
    {
        try { callback(); } catch { }
    }

    // ============================================================
    // Helpers
    // ============================================================

    private void EnsureWritable()
    {
        if (_isReadOnly)
            throw new InvalidOperationException("Transaction is read-only.");
        if (_state != TransactionState.Active)
            throw new InvalidOperationException($"Transaction is in state {_state}.");
    }

    private SqliteCommand NewCommand()
    {
        var cmd = _backend.Connection.CreateCommand();
        cmd.Transaction = _sqliteTx;
        return cmd;
    }

    private void WriteProperty(string table, string ownerColumn, long ownerId, int keyId, in PropertyValue value)
    {
        using var cmd = NewCommand();
        cmd.CommandText = $@"
            INSERT INTO {table}({ownerColumn}, key_id, value_type, int_value, double_value, text_value, blob_value)
            VALUES ($o, $k, $vt, $iv, $dv, $tv, $bv)
            ON CONFLICT({ownerColumn}, key_id) DO UPDATE SET
                value_type   = excluded.value_type,
                int_value    = excluded.int_value,
                double_value = excluded.double_value,
                text_value   = excluded.text_value,
                blob_value   = excluded.blob_value;";
        cmd.Parameters.AddWithValue("$o", ownerId);
        cmd.Parameters.AddWithValue("$k", keyId);
        cmd.Parameters.AddWithValue("$vt", (int)value.Type);

        // 値型に対応する列だけセットし、他は NULL のままにする。プレーン SQL でも
        // 行のデバッグがしやすくなる。
        object intVal = DBNull.Value, dblVal = DBNull.Value, txtVal = DBNull.Value, blbVal = DBNull.Value;
        switch (value.Type)
        {
            case PropertyValueType.Bool:
            case PropertyValueType.Int32:
            case PropertyValueType.Int64:
                intVal = value.Int64Value;
                break;
            case PropertyValueType.Double:
                dblVal = value.DoubleValue;
                break;
            case PropertyValueType.String:
                txtVal = Encoding.UTF8.GetString(value.Utf8StringValue);
                break;
            case PropertyValueType.Bytes:
                blbVal = value.BytesValue.ToArray();
                break;
        }
        cmd.Parameters.AddWithValue("$iv", intVal);
        cmd.Parameters.AddWithValue("$dv", dblVal);
        cmd.Parameters.AddWithValue("$tv", txtVal);
        cmd.Parameters.AddWithValue("$bv", blbVal);
        cmd.ExecuteNonQuery();
    }

    private PropertyValue ReadProperty(string table, string ownerColumn, long ownerId, int keyId)
    {
        using var cmd = NewCommand();
        cmd.CommandText = $@"SELECT value_type, int_value, double_value, text_value, blob_value
                             FROM {table}
                             WHERE {ownerColumn} = $o AND key_id = $k;";
        cmd.Parameters.AddWithValue("$o", ownerId);
        cmd.Parameters.AddWithValue("$k", keyId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return default;

        var vt = (PropertyValueType)reader.GetInt32(0);
        return vt switch
        {
            PropertyValueType.Bool   => PropertyValue.FromBool(reader.GetInt64(1) != 0L),
            PropertyValueType.Int32  => PropertyValue.FromInt32((int)reader.GetInt64(1)),
            PropertyValueType.Int64  => PropertyValue.FromInt64(reader.GetInt64(1)),
            PropertyValueType.Double => PropertyValue.FromDouble(reader.GetDouble(2)),
            PropertyValueType.String => PropertyValue.FromString(reader.GetString(3)),
            PropertyValueType.Bytes  => PropertyValue.FromBytes((byte[])reader.GetValue(4)),
            _ => default,
        };
    }

    private void EnsureIndexMeta(string indexName, int valueType)
    {
        using var cmd = NewCommand();
        cmd.CommandText = @"INSERT INTO index_meta(name, value_type) VALUES ($n, $vt)
                            ON CONFLICT(name) DO NOTHING;";
        cmd.Parameters.AddWithValue("$n",  indexName);
        cmd.Parameters.AddWithValue("$vt", valueType);
        cmd.ExecuteNonQuery();
    }

    private void SeekInto(List<long> sink, string indexName, string keyColumn, object keyValue)
    {
        using var cmd = NewCommand();
        cmd.CommandText = $@"SELECT node_id FROM index_entries
                             WHERE name = $n AND {keyColumn} = $k;";
        cmd.Parameters.AddWithValue("$n", indexName);
        cmd.Parameters.AddWithValue("$k", keyValue);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sink.Add(reader.GetInt64(0));
    }
}

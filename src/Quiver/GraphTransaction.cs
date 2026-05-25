using System.Diagnostics;
using Quiver.Core;
using Quiver.Core.Telemetry;
using Quiver.Logical;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

internal sealed class GraphTransaction : IGraphTransaction
{
    private readonly ITransaction _inner;
    private readonly ITokenStore<LabelId> _labelTokens;
    private readonly ITokenStore<RelationshipTypeId> _relTypeTokens;
    private readonly ITokenStore<PropertyKeyId> _propKeyTokens;
    // BA-7: null でない場合、公開ミューテーションをすべてバッファし、下層トランザクションが
    // 永続化コミットされた後にバッチをシンクへ引き渡す。
    private readonly ILogicalMutationSink? _logicalSink;
    private List<LogicalMutation>? _logicalBuffer;

    internal GraphTransaction(
        ITransaction inner,
        ITokenStore<LabelId> labelTokens,
        ITokenStore<RelationshipTypeId> relTypeTokens,
        ITokenStore<PropertyKeyId> propKeyTokens,
        bool isReadOnly = false,
        ILogicalMutationSink? logicalSink = null)
    {
        _inner = inner;
        _labelTokens = labelTokens;
        _relTypeTokens = relTypeTokens;
        _propKeyTokens = propKeyTokens;
        IsReadOnly = isReadOnly;
        _logicalSink = logicalSink;
        if (_logicalSink != null)
        {
            // WAL フラッシュが完了した後にのみバッファをシンクへ引き渡す。
            // OnCommitted フックはロールバックやコミット失敗時には発火しない。
            _inner.OnCommitted(FlushLogicalBuffer);
        }
    }

    private void RecordLogical(in LogicalMutation mutation)
    {
        if (_logicalSink == null) return;
        (_logicalBuffer ??= new List<LogicalMutation>()).Add(mutation);
    }

    private void FlushLogicalBuffer()
    {
        if (_logicalSink == null || _logicalBuffer == null || _logicalBuffer.Count == 0) return;
        _logicalSink.OnCommitted(_inner.Id, _logicalBuffer);
    }

    public TransactionId Id => _inner.Id;
    public TransactionState State => _inner.State;
    public bool IsReadOnly { get; }

    // ========== ノード操作 ==========

    public NodeId CreateNode(string label)
    {
        var labelId = _labelTokens.GetOrCreate(label);
        var nodeId = _inner.Nodes.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateNode(nodeId, label));
        return nodeId;
    }

    public NodeId CreateNode(LabelId labelId)
    {
        var nodeId = _inner.Nodes.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateNode(nodeId, _labelTokens.GetName(labelId)));
        return nodeId;
    }

    public void DeleteNode(NodeId nodeId)
    {
        // まず関連リレーションシップをすべて収集してから削除する
        var firstRelId = _inner.Nodes.Read(nodeId).FirstRelationshipId;
        var toDelete = new List<RelationshipId>();
        var relId = firstRelId;
        while (relId.IsValid)
        {
            toDelete.Add(relId);
            var rel = _inner.Relationships.Read(relId);
            relId = rel.Source == nodeId ? rel.SourceNext : rel.TargetNext;
        }
        foreach (var rid in toDelete)
            DeleteRelationship(rid);

        _inner.Nodes.Free(nodeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteNode(nodeId));
    }

    public bool NodeExists(NodeId nodeId)
        => _inner.Nodes.Read(nodeId).InUse;

    // ========== MERGE (GC-5) ==========

    // PW-18 follow-up: 「インデックス未登録」を初回 MergeNode 呼び出し時に一度だけ警告する。
    // (label, propertyKey) 単位で重複抑制。プロセス共有で問題ない (誤検出より煩いログ抑制を優先)。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), byte>
        _mergeFullscanWarned = new();

    public (NodeId Id, bool Created) MergeNode(string label, string matchKey, in PropertyValue matchValue)
    {
        var labelId = _labelTokens.GetOrCreate(label);

        // PW-18 follow-up: (label, matchKey) にインデックスが登録されていれば、
        // SeekNodesByIndex で O(log n) シーク。なければ既存のフルスキャン経路へフォールバック。
        if (_inner.Indexes.TryGetIndexName(label, matchKey, out var indexName))
        {
            foreach (var nodeId in _inner.Access.SeekNodesByIndex(_inner, indexName, matchValue))
            {
                // インデックスには削除済みノードの古いエントリが残ることがあるので生存確認。
                if (_inner.Nodes.Read(nodeId).InUse)
                    return (nodeId, false);
            }
            // ヒット無し → 新規作成へ
        }
        else
        {
            // インデックス未登録: 既存のフルスキャン経路 (label スキャン + プロパティ比較)。
            // プロパティキーが一度も観測されていない場合は確実にミスなのでスキャン省略。
            if (_propKeyTokens.TryGet(matchKey, out var keyId))
            {
                if (_mergeFullscanWarned.TryAdd((label, matchKey), 0))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "Quiver.MergeNode: (label='{0}', key='{1}') にインデックスが未登録のためフルスキャンに落ちました。" +
                        " 'Schema.CreateIndex(name, label, propertyKey, kind)' で索引を作成すると O(log n) になります。",
                        label, matchKey);
                }
                foreach (var nodeId in _inner.Access.ScanNodes(_inner, labelId))
                {
                    var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
                    if (!firstPropId.IsValid) continue;
                    var propEnum = _inner.Properties.Enumerate(firstPropId);
                    while (propEnum.MoveNext())
                    {
                        var cur = propEnum.Current;
                        if (cur.KeyId != keyId) continue;
                        if (PropertyValueEqualityHelper.AreEqual(cur.Value, in matchValue))
                            return (nodeId, false);
                        break;
                    }
                }
            }
        }

        var newId = _inner.Nodes.Allocate(labelId);
        var newKeyId = _propKeyTokens.GetOrCreate(matchKey);
        SetNodeProperty(newId, newKeyId, in matchValue);
        // PW-18 follow-up: インデックスが登録されていれば新規エントリも追加する。
        // これが無いと「初回 MergeNode は遅い、2 回目以降の MergeNode で同じキーを見つけられない」
        // 状態になり upsert セマンティクスが壊れる。
        if (!string.IsNullOrEmpty(indexName))
            InsertIntoIndex(indexName, in matchValue, newId);
        return (newId, true);
    }

    private void InsertIntoIndex(string indexName, in PropertyValue value, NodeId nodeId)
    {
        switch (value.Type)
        {
            case PropertyValueType.Bool:
            case PropertyValueType.Int32:
            case PropertyValueType.Int64:
                _inner.Indexes.CreateInt64Index(indexName).Insert(value.Int64Value, nodeId.Value);
                break;
            case PropertyValueType.Double:
                _inner.Indexes.CreateDoubleIndex(indexName).Insert(value.DoubleValue, nodeId.Value);
                break;
            case PropertyValueType.String:
            {
                var s = System.Text.Encoding.UTF8.GetString(value.Utf8StringValue);
                _inner.Indexes.CreateStringIndex(indexName).Insert(s, nodeId.Value);
                break;
            }
            // Bytes / 他は現状未対応 — フォールスルー (=フルスキャン経路と同等の安全動作)。
        }
    }

    // ========== リレーション操作 ==========

    public RelationshipId CreateRelationship(NodeId source, NodeId target, string type)
    {
        var typeId = _relTypeTokens.GetOrCreate(type);
        var relId = _inner.Relationships.Create(_inner.Nodes, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateRelationship(relId, source, target, type));
        return relId;
    }

    public RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId)
    {
        var relId = _inner.Relationships.Create(_inner.Nodes, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateRelationship(
                relId, source, target, _relTypeTokens.GetName(typeId)));
        return relId;
    }

    public void DeleteRelationship(RelationshipId relId)
    {
        FreeRelationshipProperties(relId);
        // PW-14: この ID が不変ベースビューに含まれる場合、隣接ブロックには依然として
        // 現れる — 後続の expand カーソルがスキップできるよう tombstone を記録する。
        // delta 側 ID に対してはストアは no-op。
        _inner.AdjacencyBlocks?.Tombstone(relId);
        _inner.Relationships.Delete(_inner.Nodes, relId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteRelationship(relId));
    }

    private void FreeRelationshipProperties(RelationshipId relId)
    {
        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var toDelete = new List<PropertyId>();
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
            toDelete.Add(propEnum.Current.Id);
        var currentFirst = firstPropId;
        foreach (var pid in toDelete)
            currentFirst = _inner.Properties.Delete(pid, currentFirst);
    }

    // ========== プロパティ操作 ==========

    public void SetProperty(NodeId nodeId, string key, in PropertyValue value)
    {
        var keyId = _propKeyTokens.GetOrCreate(key);
        // BA-7: SetNodeProperty がチェーンを変更する前にキャプチャする — value は
        // ref struct のため、ヒープコピーは LogicalPropertyValue に閉じ込める。
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetNodeProperty(nodeId, key, in captured));
        }
        SetNodeProperty(nodeId, keyId, in value);
    }

    public void SetProperty(RelationshipId relId, string key, in PropertyValue value)
    {
        var keyId = _propKeyTokens.GetOrCreate(key);
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetRelationshipProperty(relId, key, in captured));
        }
        SetRelationshipProperty(relId, keyId, in value);
    }

    private void SetRelationshipProperty(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
        var newFirst = firstPropId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                newFirst = _inner.Properties.Delete(propEnum.Current.Id, newFirst);
                break;
            }
        }
        var newPropId = _inner.Properties.Create(keyId, in value, newFirst);
        var wh = _inner.Relationships.Write(relId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    private void SetNodeProperty(NodeId nodeId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;

        // 既存値があれば削除する
        var newFirst = firstPropId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                newFirst = _inner.Properties.Delete(propEnum.Current.Id, newFirst);
                break;
            }
        }

        var newPropId = _inner.Properties.Create(keyId, in value, newFirst);
        var wh = _inner.Nodes.Write(nodeId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    public void RemoveProperty(NodeId nodeId, string key)
    {
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;

        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Nodes.Write(nodeId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                if (_logicalSink != null)
                    RecordLogical(LogicalMutation.RemoveNodeProperty(nodeId, key));
                return;
            }
        }
    }

    public PropertyValue GetProperty(NodeId nodeId, string key)
    {
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId) return prop.Value;
        }
        return default;
    }

    public PropertyValue GetProperty(RelationshipId relId, string key)
    {
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId) return prop.Value;
        }
        return default;
    }

    public bool HasProperty(NodeId nodeId, string key)
    {
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId) return true;
        }
        return false;
    }

    public PropertyEnumerator EnumerateProperties(NodeId nodeId)
    {
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        return _inner.Properties.Enumerate(firstPropId);
    }

    // ========== トラバーサル ==========

    public RelationshipEnumerator EnumerateRelationships(
        NodeId nodeId,
        Direction direction = Direction.Both,
        string? typeFilter = null)
    {
        if (typeFilter != null && _relTypeTokens.TryGet(typeFilter, out var typeId))
            return _inner.Relationships.EnumerateNeighbors(nodeId, _inner.Nodes, typeId, direction);

        return _inner.Relationships.EnumerateNeighbors(nodeId, _inner.Nodes);
    }

    // ========== インデックス ==========

    public void IndexInsert(string indexName, string key, NodeId nodeId)
        => _inner.Indexes.CreateStringIndex(indexName).Insert(key, nodeId.Value);

    public void IndexInsert(string indexName, long key, NodeId nodeId)
        => _inner.Indexes.CreateInt64Index(indexName).Insert(key, nodeId.Value);

    public void IndexInsert(string indexName, double key, NodeId nodeId)
        => _inner.Indexes.CreateDoubleIndex(indexName).Insert(key, nodeId.Value);

    public NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key)
    {
        IEnumerable<long> values = key.Type switch
        {
            PropertyValueType.Int32 or PropertyValueType.Int64 or PropertyValueType.Bool =>
                _inner.Indexes.CreateInt64Index(indexName).SeekValues(key.Int64Value),
            PropertyValueType.Double =>
                _inner.Indexes.CreateDoubleIndex(indexName).SeekValues(key.DoubleValue),
            PropertyValueType.String =>
                _inner.Indexes.CreateStringIndex(indexName).SeekValues(
                    System.Text.Encoding.UTF8.GetString(key.Utf8StringValue)),
            _ => [],
        };
        return new NodeIdEnumerator(values);
    }

    public NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive)
    {
        IEnumerable<long> values;
        switch (from.Type)
        {
            case PropertyValueType.Int32:
            case PropertyValueType.Int64:
            case PropertyValueType.Bool:
                values = _inner.Indexes.CreateInt64Index(indexName).RangeValues(
                    from.Int64Value, fromInclusive, to.Int64Value, toInclusive);
                break;
            case PropertyValueType.Double:
                values = _inner.Indexes.CreateDoubleIndex(indexName).RangeValues(
                    from.DoubleValue, fromInclusive, to.DoubleValue, toInclusive);
                break;
            case PropertyValueType.String:
                string fromStr = System.Text.Encoding.UTF8.GetString(from.Utf8StringValue);
                string toStr   = System.Text.Encoding.UTF8.GetString(to.Utf8StringValue);
                values = _inner.Indexes.CreateStringIndex(indexName).RangeValues(
                    fromStr, fromInclusive, toStr, toInclusive);
                break;
            default:
                values = [];
                break;
        }
        return new NodeIdEnumerator(values);
    }

    // ========== 物理プラン実行 ==========

    public QueryResult Execute(IPhysicalOperator plan)
    {
        // OB-1: query 実行全体を span + duration histogram で計測。
        using var activity = QuiverTelemetry.QueryActivitySource.StartActivity(
            "query.execute", ActivityKind.Internal);
        activity?.SetTag("quiver.tx.id", _inner.Id.Value);
        var sw = Stopwatch.StartNew();
        plan.Open(_inner);
        var rows = new List<QueryRow>();

        while (plan.MoveNext())
        {
            var cur = plan.Current;
            var slots = new TupleSlot[cur.ColumnCount];
            byte[]?[]? byteData = null;

            for (int i = 0; i < cur.ColumnCount; i++)
            {
                slots[i] = cur[i];
                if (slots[i].Type is TupleSlotType.Utf8String or TupleSlotType.Bytes)
                {
                    byteData ??= new byte[]?[cur.ColumnCount];
                    byteData[i] = plan.GetBytes(i).ToArray();
                }
            }
            rows.Add(new QueryRow(slots, byteData));
        }

        var schema = plan.Schema;
        var stats = plan.Statistics;
        plan.Dispose();
        QuiverTelemetry.QueryDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("quiver.query.rows", rows.Count);
        return new QueryResult(schema, stats, rows);
    }

    public IQueryCursor ExecuteCursor(IPhysicalOperator plan)
    {
        plan.Open(_inner);
        return new PhysicalOperatorCursor(plan);
    }

    public IAdjacencyBlockStore? AdjacencyBlocks => _inner.AdjacencyBlocks;

    public IGraphAccessMethods Access => _inner.Access;

    public void Commit() => _inner.Commit();
    public void Rollback() => _inner.Abort();
    public void Dispose() => _inner.Dispose();

    // FT-23: savepoint / nested undo — 下層トランザクションへ委譲する。
    public SavepointId Savepoint(string? name = null) => _inner.Savepoint(name);
    public void RollbackTo(SavepointId savepoint) => _inner.RollbackTo(savepoint);
    public void ReleaseSavepoint(SavepointId savepoint) => _inner.ReleaseSavepoint(savepoint);

    // VEC-3: post-commit / post-rollback フックの登録は下層トランザクションへ委譲する。
    // ユーザは IGraphTransaction 経由でフックを登録できる。
    public void OnCommitted(Action callback) => _inner.OnCommitted(callback);
    public void OnRolledBack(Action callback) => _inner.OnRolledBack(callback);
}

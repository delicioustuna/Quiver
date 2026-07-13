using System.Diagnostics;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Telemetry;
using Quiver.Logical;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

internal sealed class GraphTransaction : IGraphTransactionInternal
{
    private readonly ITransaction _inner;
    private readonly ITokenStore<LabelId> _labelTokens;
    private readonly ITokenStore<RelationshipTypeId> _relTypeTokens;
    private readonly PropertyKeyTokenStore _propKeyTokens;
    // null でない場合、公開ミューテーションをすべてバッファし、下層トランザクションが
    // 永続化コミットされた後にバッチをシンクへ引き渡す。
    private readonly ITokenStore<HyperedgeTypeId> _hyperedgeTypeTokens;
    private readonly ITokenStore<RoleId> _roleTokens;
    private readonly ILogicalMutationSink? _logicalSink;
    private List<LogicalMutation>? _logicalBuffer;
    private readonly Storage.Records.ColumnManager? _columns;
    private readonly Core.IVectorStore? _vectors;

    internal GraphTransaction(
        ITransaction inner,
        ITokenStore<LabelId> labelTokens,
        ITokenStore<RelationshipTypeId> relTypeTokens,
        PropertyKeyTokenStore propKeyTokens,
        ITokenStore<HyperedgeTypeId> hyperedgeTypeTokens,
        ITokenStore<RoleId> roleTokens,
        bool isReadOnly = false,
        ILogicalMutationSink? logicalSink = null,
        Storage.Records.ColumnManager? columns = null,
        Core.IVectorStore? vectors = null)
    {
        _inner = inner;
        _labelTokens = labelTokens;
        _relTypeTokens = relTypeTokens;
        _propKeyTokens = propKeyTokens;
        _hyperedgeTypeTokens = hyperedgeTypeTokens;
        _roleTokens = roleTokens;
        IsReadOnly = isReadOnly;
        _logicalSink = logicalSink;
        _vectors = vectors;
        // 登録済み列があるときだけ列維持を有効化し、ホット path の
        // 余計な hook 登録 / dict lookup を避ける。
        _columns = columns is { HasAnyColumns: true } ? columns : null;
        if (_logicalSink != null)
        {
            // WAL フラッシュが完了した後にのみバッファをシンクへ引き渡す。
            // OnCommitted フックはロールバックやコミット失敗時には発火しない。
            _inner.OnCommitted(FlushLogicalBuffer);
        }
        if (_columns != null)
        {
            // abort 後: before-image undo + ReloadColumns で head は復元済み。中止 tx が
            // 積んだ delta 版 (不可視ゴミ) をこの tx スコープで掃除する。
            _inner.OnRolledBack(() => _columns.PruneAbortedTx(_inner.Id.Value));
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

    // MigrationContext.ForEachNode が Access.ScanNodes に渡す。
    internal ITransaction Inner => _inner;

    private TransactionUsageLease EnterUsage() => _inner.EnterUsage();

    // ========== ノード操作 ==========

    public NodeId CreateNode(string label)
    {
        using var usage = EnterUsage();
        var labelId = _labelTokens.GetOrCreate(label);
        var nodeId = _inner.Nodes.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateNode(nodeId, label));
        return nodeId;
    }

    public NodeId CreateNode(LabelId labelId)
    {
        using var usage = EnterUsage();
        var nodeId = _inner.Nodes.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateNode(nodeId, _labelTokens.GetName(labelId)));
        return nodeId;
    }

    public void DeleteNode(NodeId nodeId)
    {
        using var usage = EnterUsage();
        var firstRelId = _inner.Nodes.Read(nodeId).FirstRelationshipId;
        var relsToDelete = new List<RelationshipId>();
        var relId = firstRelId;
        while (relId.IsValid)
        {
            relsToDelete.Add(relId);
            var rel = _inner.Relationships.Read(relId);
            relId = rel.Source.Sequence == nodeId.Sequence ? rel.SourceNext : rel.TargetNext;
        }
        foreach (var rid in relsToDelete)
            DeleteRelationship(rid);

        var heToDelete = new HashSet<long>();
        var incEnum = _inner.Incidences.EnumerateByNode(
            nodeId, _inner.NodeIncidenceHeads, _inner.Hyperedges);
        while (incEnum.MoveNext())
            heToDelete.Add(incEnum.Current.HyperedgeId.Sequence);
        foreach (var heSeq in heToDelete)
            DeleteHyperedge(new HyperedgeId(heSeq));

        if (_inner.Indexes.HasAnyFullTextIndex)
            RemoveNodeFromFullTextIndexes(nodeId);

        _inner.Nodes.Free(nodeId);
        _columns?.OnDeleteEntity(Core.EntityKind.Node, nodeId.Sequence, _inner.Id.Value);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteNode(nodeId));
    }

    public bool NodeExists(NodeId nodeId)
    {
        using var usage = EnterUsage();
        return _inner.Nodes.Read(nodeId).InUse;
    }

    public string? GetNodeLabel(NodeId nodeId)
    {
        using var usage = EnterUsage();
        var node = _inner.Nodes.Read(nodeId);
        if (!node.InUse || !node.Label.IsValid) return null;
        return _labelTokens.GetName(node.Label);
    }

    public string? GetRelationshipTypeName(RelationshipTypeId typeId)
        => typeId.IsValid ? _relTypeTokens.GetName(typeId) : null;

    // ========== MERGE ==========

    // 「インデックス未登録」を初回 MergeNode 呼び出し時に一度だけ警告する。
    // (label, propertyKey) 単位で重複抑制。プロセス共有で問題ない (誤検出より煩いログ抑制を優先)。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), byte>
        _mergeFullscanWarned = new();

    public (NodeId Id, bool Created) MergeNode(string label, string matchKey, in PropertyValue matchValue)
    {
        using var usage = EnterUsage();
        var labelId = _labelTokens.GetOrCreate(label);

        // (label, matchKey) にインデックスが登録されていれば、
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
                    // inline + overflow を結合列挙 (inline のみのノードも拾う)。
                    var propEnum = _inner.Nodes.EnumerateProperties(nodeId, _inner.Properties);
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
        // インデックスが登録されていれば新規エントリも追加する。
        // これが無いと「初回 MergeNode は遅い、2 回目以降の MergeNode で同じキーを見つけられない」
        // 状態になり upsert セマンティクスが壊れる。
        if (!string.IsNullOrEmpty(indexName))
            InsertIntoIndex(indexName, in matchValue, newId);
        return (newId, true);
    }

    private void InsertIntoIndex(string indexName, in PropertyValue value, NodeId nodeId)
    {
        long packed = PackNode(nodeId);
        switch (value.Type)
        {
            case PropertyValueType.Bool:
            case PropertyValueType.Int32:
            case PropertyValueType.Int64:
                _inner.Indexes.CreateInt64Index(indexName).Insert(value.Int64Value, packed);
                break;
            case PropertyValueType.Double:
                _inner.Indexes.CreateDoubleIndex(indexName).Insert(value.DoubleValue, packed);
                break;
            case PropertyValueType.String:
            {
                var s = System.Text.Encoding.UTF8.GetString(value.Utf8StringValue);
                _inner.Indexes.CreateStringIndex(indexName).Insert(s, packed);
                break;
            }
            // Bytes / 他は現状未対応 — フォールスルー (=フルスキャン経路と同等の安全動作)。
        }
    }

    private void RemoveFromIndex(string indexName, in PropertyValue value, NodeId nodeId)
    {
        long packed = PackNode(nodeId);
        switch (value.Type)
        {
            case PropertyValueType.Bool:
            case PropertyValueType.Int32:
            case PropertyValueType.Int64:
                _inner.Indexes.CreateInt64Index(indexName).Delete(value.Int64Value, packed);
                break;
            case PropertyValueType.Double:
                _inner.Indexes.CreateDoubleIndex(indexName).Delete(value.DoubleValue, packed);
                break;
            case PropertyValueType.String:
            {
                var s = System.Text.Encoding.UTF8.GetString(value.Utf8StringValue);
                _inner.Indexes.CreateStringIndex(indexName).Delete(s, packed);
                break;
            }
        }
    }

    private bool TryResolveSecondaryIndex(NodeId nodeId, string key, out string indexName)
    {
        var node = _inner.Nodes.Read(nodeId);
        if (!node.InUse || !node.Label.IsValid) { indexName = string.Empty; return false; }
        var labelName = _labelTokens.GetName(node.Label);
        if (string.IsNullOrEmpty(labelName)) { indexName = string.Empty; return false; }
        return _inner.Indexes.TryGetIndexName(labelName, key, out indexName);
    }

    // 索引の値レーンに (Kind=Node, Sequence=nodeId, Generation=現世代) をパックする。
    // 解決時に現 slot 世代と照合して slot 再利用 (ABA) の stale 参照を弾けるようにする。
    private long PackNode(NodeId nodeId)
        => EntityRef.Pack(EntityKind.Node, nodeId.Sequence, _inner.Nodes.CurrentGeneration(nodeId.Sequence));

    // ノードの (label, key) に bound された全文索引を引く。FT 索引がゼロなら fast-path で null。
    private FullTextIndex? ResolveFullTextIndex(NodeId nodeId, string key)
    {
        if (!_inner.Indexes.HasAnyFullTextIndex) return null;
        var node = _inner.Nodes.Read(nodeId);
        if (!node.InUse || !node.Label.IsValid) return null;
        var labelName = _labelTokens.GetName(node.Label);
        if (string.IsNullOrEmpty(labelName)) return null;
        return _inner.Indexes.TryGetFullTextIndexByLabelKey(labelName, key, out var ft) ? ft : null;
    }

    // ノードの現在の string プロパティ値 (before-image) を読む。非 string / 未設定なら null。
    private string? ReadNodeStringProperty(NodeId nodeId, PropertyKeyId keyId)
    {
        var e = _inner.Nodes.EnumerateProperties(nodeId, _inner.Properties);
        while (e.MoveNext())
        {
            if (e.Current.KeyId != keyId) continue;
            var v = e.Current.Value;
            return v.Type == PropertyValueType.String
                ? System.Text.Encoding.UTF8.GetString(v.Utf8StringValue) : null;
        }
        return null;
    }

    // ノード削除時に、その string プロパティを bound 全文索引から除去する。
    private void RemoveNodeFromFullTextIndexes(NodeId nodeId)
    {
        var node = _inner.Nodes.Read(nodeId);
        if (!node.InUse || !node.Label.IsValid) return;
        var labelName = _labelTokens.GetName(node.Label);
        if (string.IsNullOrEmpty(labelName)) return;
        long entityId = PackNode(nodeId);
        // span は MoveNext で無効化されるため、文字列を先に materialize してから除去する。
        var docs = new List<(FullTextIndex Ft, string Text)>();
        var e = _inner.Nodes.EnumerateProperties(nodeId, _inner.Properties);
        while (e.MoveNext())
        {
            var v = e.Current.Value;
            if (v.Type != PropertyValueType.String) continue;
            var keyName = _propKeyTokens.GetName(e.Current.KeyId);
            if (string.IsNullOrEmpty(keyName)) continue;
            if (_inner.Indexes.TryGetFullTextIndexByLabelKey(labelName, keyName, out var ft))
                docs.Add((ft, System.Text.Encoding.UTF8.GetString(v.Utf8StringValue)));
        }
        foreach (var (ft, text) in docs)
            _inner.Indexes.MaintainFullText(ft, entityId, text, null);
    }

    // ========== リレーション操作 ==========

    public RelationshipId CreateRelationship(NodeId source, NodeId target, string type)
    {
        using var usage = EnterUsage();
        var typeId = _relTypeTokens.GetOrCreate(type);
        var relId = _inner.Relationships.Create(_inner.Nodes, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateRelationship(relId, source, target, type));
        return relId;
    }

    public RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId)
    {
        using var usage = EnterUsage();
        var relId = _inner.Relationships.Create(_inner.Nodes, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateRelationship(
                relId, source, target, _relTypeTokens.GetName(typeId)));
        return relId;
    }

    public (RelationshipId Id, bool Created) MergeRelationship(NodeId source, NodeId target, string type)
    {
        using var usage = EnterUsage();
        // 型トークンが未観測なら、その型のエッジは存在し得ない → 走査せず直接作成。
        // (公開 EnumerateRelationships は型未知のとき全隣接へフォールバックするため、ここでは
        // store の typed + Outgoing 列挙を直接使い、別型エッジを target 一致で誤マッチしないようにする。)
        if (_relTypeTokens.TryGet(type, out var typeId))
        {
            var e = _inner.Relationships.EnumerateNeighbors(source, _inner.Nodes, typeId, Direction.Outgoing);
            while (e.MoveNext())
            {
                // Outgoing 列挙では Current.Source == source が保証されるので Target だけ照合する。
                if (e.Current.Target == target)
                    return (e.Current.Id, false);
            }
        }
        return (CreateRelationship(source, target, type), true);
    }

    public void DeleteRelationship(RelationshipId relId)
    {
        using var usage = EnterUsage();
        if (!_inner.Relationships.Read(relId).InUse)
            return;
        FreeRelationshipProperties(relId);
        // この ID が不変ベースビューに含まれる場合、隣接ブロックには依然として
        // 現れる — 後続の expand カーソルがスキップできるよう tombstone を記録する。
        // delta 側 ID に対してはストアは no-op。
        _inner.AdjacencyBlocks?.Tombstone(relId);
        _inner.Relationships.Delete(_inner.Nodes, relId);
        // リレーション削除に伴い、その kind の全列で seq を論理削除する。
        _columns?.OnDeleteEntity(Core.EntityKind.Relationship, relId.Sequence, _inner.Id.Value);
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
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key);
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use AddPropertyValue for Set-cardinality property '{key}'.");
        // 透過維持: この (label, key) に全文索引が bound されているときだけ before-image を読む。
        // 非索引キーの書き込みは HasAnyFullTextIndex の bool チェックのみで素通り。
        var ft = ResolveFullTextIndex(nodeId, key);
        string? oldText = ft is null ? null : ReadNodeStringProperty(nodeId, keyId);
        // SetNodeProperty がチェーンを変更する前にキャプチャする — value は
        // ref struct のため、ヒープコピーは LogicalPropertyValue に閉じ込める。
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetNodeProperty(nodeId, key, in captured));
        }
        SetNodeProperty(nodeId, keyId, in value);
        // 列化済み key なら同 tx で列を維持する。
        _columns?.OnSetProperty(Core.EntityKind.Node, nodeId.Sequence, keyId, in value, _inner.Id.Value);
        if (ft is not null)
        {
            string? newText = value.Type == PropertyValueType.String
                ? System.Text.Encoding.UTF8.GetString(value.Utf8StringValue) : null;
            if (oldText is not null || newText is not null)
                _inner.Indexes.MaintainFullText(ft, PackNode(nodeId), oldText, newText);
        }
    }

    public void SetProperty(RelationshipId relId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key);
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use AddPropertyValue for Set-cardinality property '{key}'.");
        if (!_inner.Relationships.Read(relId).InUse)
            return;
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetRelationshipProperty(relId, key, in captured));
        }
        SetRelationshipProperty(relId, keyId, in value);
        // 列化済み key なら同 tx で列を維持する。
        _columns?.OnSetProperty(Core.EntityKind.Relationship, relId.Sequence, keyId, in value, _inner.Id.Value);
    }

    private void SetRelationshipProperty(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value)
    {
        // 小さい値は rel record へ inline (copy-on-write)。
        if (InlinePropertyCodec.IsInlineable(value) && _inner.Relationships.SetInlineProperty(relId, keyId, in value))
        {
            // size-class 変更で同 key が overflow に残っていれば除去する。
            RemoveRelOverflowIfPresent(relId, keyId);
            return;
        }
        // inline 不可 / 予算超過 → overflow チェーン。inline 側に旧値があれば除去。
        _inner.Relationships.RemoveInlineProperty(relId, keyId);
        SetRelationshipOverflow(relId, keyId, in value);
    }

    private void SetRelationshipOverflow(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value)
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

    private void RemoveRelOverflowIfPresent(RelationshipId relId, PropertyKeyId keyId)
    {
        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Relationships.Write(relId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    private void SetNodeProperty(NodeId nodeId, PropertyKeyId keyId, in PropertyValue value)
    {
        // 小さい値は node record へ inline (copy-on-write)。
        if (InlinePropertyCodec.IsInlineable(value) && _inner.Nodes.SetInlineProperty(nodeId, keyId, in value))
        {
            // size-class 変更で同 key が overflow に残っていれば除去する。
            RemoveNodeOverflowIfPresent(nodeId, keyId);
            return;
        }
        // inline 不可 / 予算超過 → overflow チェーン。inline 側に旧値があれば除去。
        _inner.Nodes.RemoveInlineProperty(nodeId, keyId);
        SetNodeOverflow(nodeId, keyId, in value);
    }

    private void SetNodeOverflow(NodeId nodeId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
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

    private void RemoveNodeOverflowIfPresent(NodeId nodeId, PropertyKeyId keyId)
    {
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Nodes.Write(nodeId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    public void RemoveProperty(NodeId nodeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;

        // inline を先に試し、無ければ overflow チェーンから除去。
        bool removed = _inner.Nodes.RemoveInlineProperty(nodeId, keyId);
        if (!removed)
        {
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
                    removed = true;
                    break;
                }
            }
        }
        if (removed)
        {
        // 列化済み key なら列も論理削除する。
            _columns?.OnRemoveProperty(Core.EntityKind.Node, nodeId.Sequence, keyId, _inner.Id.Value);
            if (_logicalSink != null)
                RecordLogical(LogicalMutation.RemoveNodeProperty(nodeId, key));
        }
    }

    public PropertyValue GetProperty(NodeId nodeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use GetPropertyValues for Set-cardinality property '{key}'.");
        // inline を先に引き、無ければ overflow チェーンを walk。
        if (_inner.Nodes.TryGetInlineProperty(nodeId, keyId, out var inlineVal)) return inlineVal;
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
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use GetPropertyValues for Set-cardinality property '{key}'.");
        // inline を先に引き、無ければ overflow チェーンを walk。
        if (_inner.Relationships.TryGetInlineProperty(relId, keyId, out var inlineVal)) return inlineVal;
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
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        if (_inner.Nodes.HasInlineProperty(nodeId, keyId)) return true;
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
        using var usage = EnterUsage();
        // inline (visible 版) + overflow チェーンを結合して列挙。
        return _inner.Nodes.EnumerateProperties(nodeId, _inner.Properties);
    }

    // ========== マルチバリュープロパティ操作 (Set cardinality) ==========

    public void AddPropertyValue(NodeId nodeId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key, PropertyCardinality.Set);

        // 重複チェック: 同一 key+value の visible エントリがあればスキップ (Set セマンティクス)
        var propEnum = _inner.Nodes.EnumerateProperties(nodeId, _inner.Properties);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
                return;
        }

        // overflow チェーンの head に prepend (既存 same-key エントリは削除しない)
        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        var newPropId = _inner.Properties.Create(keyId, in value, firstPropId);
        var wh = _inner.Nodes.Write(nodeId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();

        if (TryResolveSecondaryIndex(nodeId, key, out var indexName))
            InsertIntoIndex(indexName, in value, nodeId);
    }

    public void AddPropertyValue(RelationshipId relId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key, PropertyCardinality.Set);
        if (!_inner.Relationships.Read(relId).InUse)
            return;

        var propEnum = _inner.Relationships.EnumerateProperties(relId, _inner.Properties);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
                return;
        }

        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
        var newPropId = _inner.Properties.Create(keyId, in value, firstPropId);
        var wh = _inner.Relationships.Write(relId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    public void RemovePropertyValue(NodeId nodeId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");

        var firstPropId = _inner.Nodes.Read(nodeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && propEnum.Current.InUse
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Nodes.Write(nodeId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();

                if (TryResolveSecondaryIndex(nodeId, key, out var indexName))
                    RemoveFromIndex(indexName, in value, nodeId);
                return;
            }
        }
    }

    public void RemovePropertyValue(RelationshipId relId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");
        if (!_inner.Relationships.Read(relId).InUse)
            return;

        var firstPropId = _inner.Relationships.Read(relId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && propEnum.Current.InUse
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Relationships.Write(relId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    public PropertyValuesEnumerator GetPropertyValues(NodeId nodeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
            return new PropertyValuesEnumerator(
                new PropertyEnumerator(null!, PropertyId.Invalid), default);
        return new PropertyValuesEnumerator(
            _inner.Nodes.EnumerateProperties(nodeId, _inner.Properties), keyId);
    }

    public PropertyValuesEnumerator GetPropertyValues(RelationshipId relId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
            return new PropertyValuesEnumerator(
                new PropertyEnumerator(null!, PropertyId.Invalid), default);
        return new PropertyValuesEnumerator(
            _inner.Relationships.EnumerateProperties(relId, _inner.Properties), keyId);
    }

    // ========== トラバーサル ==========

    public RelationshipEnumerator EnumerateRelationships(
        NodeId nodeId,
        Direction direction = Direction.Both,
        string? typeFilter = null)
    {
        using var usage = EnterUsage();
        if (typeFilter != null && _relTypeTokens.TryGet(typeFilter, out var typeId))
            return _inner.Relationships.EnumerateNeighbors(nodeId, _inner.Nodes, typeId, direction);

        return _inner.Relationships.EnumerateNeighbors(nodeId, _inner.Nodes);
    }

    // ========== インデックス ==========

    public void IndexInsert(string indexName, string key, NodeId nodeId)
    {
        using var usage = EnterUsage();
        _inner.Indexes.CreateStringIndex(indexName).Insert(key, PackNode(nodeId));
    }

    public void IndexInsert(string indexName, long key, NodeId nodeId)
    {
        using var usage = EnterUsage();
        _inner.Indexes.CreateInt64Index(indexName).Insert(key, PackNode(nodeId));
    }

    public void IndexInsert(string indexName, double key, NodeId nodeId)
    {
        using var usage = EnterUsage();
        _inner.Indexes.CreateDoubleIndex(indexName).Insert(key, PackNode(nodeId));
    }

    public NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key)
    {
        using var usage = EnterUsage();
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
        // パック値を世代照合しつつ NodeId.Value へ unpack する。
        return new NodeIdEnumerator(IndexValueResolver.ResolveLiveNodeSequences(values, _inner.Nodes));
    }

    public NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive)
    {
        using var usage = EnterUsage();
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
        // パック値を世代照合しつつ NodeId.Value へ unpack する。
        return new NodeIdEnumerator(IndexValueResolver.ResolveLiveNodeSequences(values, _inner.Nodes));
    }

    // ========== 物理プラン実行 ==========

    public QueryResult Execute(IPhysicalOperator plan)
    {
        using var usage = EnterUsage();
        // query 実行全体を span + duration histogram で計測。
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
            // 結果 NodeId 列に現世代を load (round-trip 一貫)。
            QueryRowMaterializer.StampEntityGenerations(
                slots, _inner.Nodes, _inner.Relationships, _inner.Hyperedges);
            rows.Add(new QueryRow(slots, byteData));
        }

        var schema = plan.Schema;
        var stats = plan.Statistics;
        plan.Dispose();
        QuiverTelemetry.QueryDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("quiver.query.rows", rows.Count);
        // EventSource で件数 + 経過時間を発行。N+1 検出や hot operator 推定に有効。
        QuiverEventSource.Log.QueryExecuted(
            _inner.Id.Value,
            rows.Count,
            sw.Elapsed.TotalMilliseconds);
        return new QueryResult(schema, stats, rows);
    }

    public IQueryCursor ExecuteCursor(IPhysicalOperator plan)
    {
        using var usage = EnterUsage();
        plan.Open(_inner);
        return new PhysicalOperatorCursor(
            plan, _inner.Nodes, _inner.Relationships, _inner.Hyperedges);
    }

    public IAdjacencyBlockStore? AdjacencyBlocks => _inner.AdjacencyBlocks;

    public IGraphAccessMethods Access => _inner.Access;

        // full-scan 集約の列スキャン経路。列が無い / mixed / committed registry
    // 無し (旧テスト互換経路) では false を返し、呼び出し側 (GraphTraversal) が row path へ。
    public bool TryColumnAggregate(Core.EntityKind kind, string key, out ColumnAggregate result)
    {
        using var usage = EnterUsage();
        result = default;
        if (_columns == null) return false;
        if (_inner.Committed is not { } committed) return false;
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        if (!_columns.TryAggregate(kind, keyId.Value, _inner.Snapshot, _inner.Id, committed,
                out long count, out double sum, out double min, out double max, out long longSum, out var vt))
            return false;
        result = new ColumnAggregate(count, sum, min, max, longSum, vt);
        return true;
    }

    // ==========: ベクトル (tx 配下) ==========

    public void SetVector(Core.EntityKind kind, long entityId, string indexName, ReadOnlySpan<float> vector)
    {
        using var usage = EnterUsage();
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot SetVector in a read-only transaction.");
        if (_vectors is null)
            throw new NotSupportedException("This backend does not support transaction-scoped SetVector.");
        // tx の ambient WalPageContext 下で書く → グラフ変更と同じ WAL に乗り、
        // commit で原子確定 / abort・crash で CLR undo により巻き戻る。
        _vectors.SetVector(kind, entityId, indexName, vector);
    }

    public void RemoveVector(Core.EntityKind kind, long entityId, string indexName)
    {
        using var usage = EnterUsage();
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot RemoveVector in a read-only transaction.");
        if (_vectors is null)
            throw new NotSupportedException("This backend does not support transaction-scoped RemoveVector.");
        _vectors.RemoveVector(kind, entityId, indexName);
    }

    public bool TryGetVector(Core.EntityKind kind, long entityId, string indexName, Span<float> destination)
    {
        using var usage = EnterUsage();
        if (_vectors is null) return false;
        return _vectors.TryGetVector(kind, entityId, indexName, destination);
    }

    // ========== ハイパーエッジ操作 ==========

    public HyperedgeId CreateHyperedge(string type, ReadOnlySpan<HyperedgeMember> members)
    {
        using var usage = EnterUsage();
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Hyperedge type must not be null, empty, or whitespace.", nameof(type));
        var typeId = _hyperedgeTypeTokens.GetOrCreate(type);
        return CreateHyperedgeCore(typeId, members);
    }

    public HyperedgeId CreateHyperedge(HyperedgeTypeId typeId, ReadOnlySpan<HyperedgeMember> members)
    {
        using var usage = EnterUsage();
        if (!typeId.IsValid)
            throw new ArgumentException("Invalid hyperedge type ID.", nameof(typeId));
        return CreateHyperedgeCore(typeId, members);
    }

    private HyperedgeId CreateHyperedgeCore(HyperedgeTypeId typeId, ReadOnlySpan<HyperedgeMember> members)
    {
        if (members.Length < 2)
            throw new ArgumentException("A hyperedge requires at least 2 members.", nameof(members));

        Span<IncidenceMember> resolved = members.Length <= 16
            ? stackalloc IncidenceMember[members.Length]
            : new IncidenceMember[members.Length];

        var seen = new HashSet<(int, long)>();
        for (int i = 0; i < members.Length; i++)
        {
            var m = members[i];
            if (string.IsNullOrWhiteSpace(m.Role))
                throw new ArgumentException($"Member role at index {i} must not be null, empty, or whitespace.", nameof(members));

            var node = _inner.Nodes.Read(m.NodeId);
            if (!node.InUse)
                throw new ArgumentException($"Node {m.NodeId} at index {i} does not exist or is not visible.", nameof(members));

            var roleId = _roleTokens.GetOrCreate(m.Role);
            if (!seen.Add((roleId.Value, m.NodeId.Sequence)))
                throw new ArgumentException($"Duplicate (Role, NodeId) pair at index {i}: ({m.Role}, {m.NodeId}).", nameof(members));

            resolved[i] = new IncidenceMember(m.NodeId, roleId);
        }

        var hyperedgeId = _inner.Hyperedges.Create(typeId, resolved, _inner.Incidences, _inner.NodeIncidenceHeads);

        // 論理ストリームは自己完結させる — メンバーは型名とロール名 (トークン ID ではなく)
        // で保持し、別 DB への再生時にターゲット側の ID へ再マッピングできるようにする。
        if (_logicalSink != null)
        {
            var typeName = _hyperedgeTypeTokens.GetName(typeId);
            var captured = new HyperedgeMember[members.Length];
            for (int i = 0; i < members.Length; i++)
                captured[i] = members[i];
            RecordLogical(LogicalMutation.CreateHyperedge(hyperedgeId, typeName, captured));
        }
        return hyperedgeId;
    }

    public void DeleteHyperedge(HyperedgeId hyperedgeId)
    {
        using var usage = EnterUsage();
        // header の可視性が incidence とプロパティの可視性の正本 — header を論理削除すれば
        // それらも同スナップショットで不可視になる。overflow プロパティレコードは物理的に残る
        // ため、slot を回収できるよう先にチェーンを解放してから header をスタンプする。
        FreeHyperedgeProperties(hyperedgeId);
        _inner.Hyperedges.Delete(hyperedgeId);
        _columns?.OnDeleteEntity(Core.EntityKind.Hyperedge, hyperedgeId.Sequence, _inner.Id.Value);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteHyperedge(hyperedgeId));
    }

    private void FreeHyperedgeProperties(HyperedgeId hyperedgeId)
    {
        var firstPropId = _inner.Hyperedges.Read(hyperedgeId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var toDelete = new List<PropertyId>();
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
            toDelete.Add(propEnum.Current.Id);
        var currentFirst = firstPropId;
        foreach (var pid in toDelete)
            currentFirst = _inner.Properties.Delete(pid, currentFirst);
    }

    public HyperedgeMemberEnumerator GetMembers(HyperedgeId hyperedgeId, string? role = null)
    {
        using var usage = EnterUsage();
        RoleId roleFilter = RoleId.Invalid;
        if (role != null && _roleTokens.TryGet(role, out var rid))
            roleFilter = rid;
        else if (role != null)
            return default;

        var innerEnum = _inner.Incidences.EnumerateByHyperedge(hyperedgeId, _inner.Hyperedges);
        return new HyperedgeMemberEnumerator(innerEnum, _roleTokens, roleFilter);
    }

    public HyperedgeIdEnumerator GetHyperedges(NodeId nodeId, string? type = null, string? role = null)
    {
        using var usage = EnterUsage();
        HyperedgeTypeId typeFilter = HyperedgeTypeId.Invalid;
        if (type != null && _hyperedgeTypeTokens.TryGet(type, out var tid))
            typeFilter = tid;
        else if (type != null)
            return default;

        RoleId roleFilter = RoleId.Invalid;
        if (role != null && _roleTokens.TryGet(role, out var rid))
            roleFilter = rid;
        else if (role != null)
            return default;

        var innerEnum = _inner.Incidences.EnumerateByNode(
            nodeId, _inner.NodeIncidenceHeads, _inner.Hyperedges);
        return new HyperedgeIdEnumerator(innerEnum, _inner.Hyperedges, typeFilter, roleFilter);
    }

    public string? GetHyperedgeTypeName(HyperedgeTypeId typeId)
        => typeId.IsValid ? _hyperedgeTypeTokens.GetName(typeId) : null;

    // ========== ハイパーエッジプロパティ操作 ==========
    // node / rel と同じプロパティエンティティとして扱う。header 15 バイト固定領域の後ろへ
    // inline 符号化し、収まらない値は既存 PropertyStore の overflow チェーンへ回す。

    public void SetProperty(HyperedgeId hyperedgeId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key);
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use AddPropertyValue for Set-cardinality property '{key}'.");
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetHyperedgeProperty(hyperedgeId, key, in captured));
        }
        SetHyperedgeProperty(hyperedgeId, keyId, in value);
        // 列化済み key なら同 tx で列を維持する (列作成の公開糖衣は後続タスク)。
        _columns?.OnSetProperty(Core.EntityKind.Hyperedge, hyperedgeId.Sequence, keyId, in value, _inner.Id.Value);
    }

    private void SetHyperedgeProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId, in PropertyValue value)
    {
        // 小さい値は header へ inline (copy-on-write)。
        if (InlinePropertyCodec.IsInlineable(value) && _inner.Hyperedges.SetInlineProperty(hyperedgeId, keyId, in value))
        {
            // size-class 変更で同 key が overflow に残っていれば除去する。
            RemoveHyperedgeOverflowIfPresent(hyperedgeId, keyId);
            return;
        }
        // inline 不可 / 予算超過 → overflow チェーン。inline 側に旧値があれば除去。
        _inner.Hyperedges.RemoveInlineProperty(hyperedgeId, keyId);
        SetHyperedgeOverflow(hyperedgeId, keyId, in value);
    }

    private void SetHyperedgeOverflow(HyperedgeId hyperedgeId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Hyperedges.Read(hyperedgeId).FirstPropertyId;
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
        var wh = _inner.Hyperedges.Write(hyperedgeId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    private void RemoveHyperedgeOverflowIfPresent(HyperedgeId hyperedgeId, PropertyKeyId keyId)
    {
        var firstPropId = _inner.Hyperedges.Read(hyperedgeId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Hyperedges.Write(hyperedgeId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    public PropertyValue GetProperty(HyperedgeId hyperedgeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use GetPropertyValues for Set-cardinality property '{key}'.");
        // inline を先に引き、無ければ overflow チェーンを walk。
        if (_inner.Hyperedges.TryGetInlineProperty(hyperedgeId, keyId, out var inlineVal)) return inlineVal;
        var firstPropId = _inner.Hyperedges.Read(hyperedgeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId) return prop.Value;
        }
        return default;
    }

    public bool HasProperty(HyperedgeId hyperedgeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        if (_inner.Hyperedges.HasInlineProperty(hyperedgeId, keyId)) return true;
        var firstPropId = _inner.Hyperedges.Read(hyperedgeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId) return true;
        }
        return false;
    }

    public void RemoveProperty(HyperedgeId hyperedgeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;

        // inline を先に試し、無ければ overflow チェーンから除去。
        bool removed = _inner.Hyperedges.RemoveInlineProperty(hyperedgeId, keyId);
        if (!removed)
        {
            var firstPropId = _inner.Hyperedges.Read(hyperedgeId).FirstPropertyId;
            var propEnum = _inner.Properties.Enumerate(firstPropId);
            while (propEnum.MoveNext())
            {
                if (propEnum.Current.KeyId == keyId)
                {
                    var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                    var wh = _inner.Hyperedges.Write(hyperedgeId);
                    wh.FirstPropertyId = newFirst;
                    wh.Dispose();
                    removed = true;
                    break;
                }
            }
        }
        if (removed)
        {
            _columns?.OnRemoveProperty(Core.EntityKind.Hyperedge, hyperedgeId.Sequence, keyId, _inner.Id.Value);
            if (_logicalSink != null)
                RecordLogical(LogicalMutation.RemoveHyperedgeProperty(hyperedgeId, key));
        }
    }

    public PropertyEnumerator EnumerateProperties(HyperedgeId hyperedgeId)
    {
        using var usage = EnterUsage();
        return _inner.Hyperedges.EnumerateProperties(hyperedgeId, _inner.Properties);
    }

    // ── ハイパーエッジのマルチバリュープロパティ (Set cardinality) ──

    public void AddPropertyValue(HyperedgeId hyperedgeId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key, PropertyCardinality.Set);

        // 重複チェック: 同一 key+value の visible エントリがあればスキップ (Set セマンティクス)。
        var scan = _inner.Hyperedges.EnumerateProperties(hyperedgeId, _inner.Properties);
        while (scan.MoveNext())
        {
            if (scan.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(scan.Current.Value, in value))
                return;
        }

        // overflow チェーンの head に prepend (既存 same-key エントリは削除しない)。
        var firstPropId = _inner.Hyperedges.Read(hyperedgeId).FirstPropertyId;
        var newPropId = _inner.Properties.Create(keyId, in value, firstPropId);
        var wh = _inner.Hyperedges.Write(hyperedgeId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();

        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.AddHyperedgePropertyValue(hyperedgeId, key, in captured));
        }
    }

    public void RemovePropertyValue(HyperedgeId hyperedgeId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");

        var firstPropId = _inner.Hyperedges.Read(hyperedgeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && propEnum.Current.InUse
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Hyperedges.Write(hyperedgeId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();

                if (_logicalSink != null)
                {
                    var captured = LogicalPropertyValue.Capture(in value);
                    RecordLogical(LogicalMutation.RemoveHyperedgePropertyValue(hyperedgeId, key, in captured));
                }
                return;
            }
        }
    }

    public PropertyValuesEnumerator GetPropertyValues(HyperedgeId hyperedgeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
            return new PropertyValuesEnumerator(
                new PropertyEnumerator(null!, PropertyId.Invalid), default);
        return new PropertyValuesEnumerator(
            _inner.Hyperedges.EnumerateProperties(hyperedgeId, _inner.Properties), keyId);
    }

    public void Commit()
    {
        using var usage = EnterUsage();
        _inner.Commit();
    }

    public void Rollback()
    {
        using var usage = EnterUsage();
        _inner.Abort();
    }

    public void Dispose()
    {
        using var usage = EnterUsage();
        _inner.Dispose();
    }

    // savepoint / nested undo — 下層トランザクションへ委譲する。
    public SavepointId Savepoint(string? name = null)
    {
        using var usage = EnterUsage();
        return _inner.Savepoint(name);
    }

    public void RollbackTo(SavepointId savepoint)
    {
        using var usage = EnterUsage();
        _inner.RollbackTo(savepoint);
    }

    public void ReleaseSavepoint(SavepointId savepoint)
    {
        using var usage = EnterUsage();
        _inner.ReleaseSavepoint(savepoint);
    }

    // post-commit / post-rollback フックの登録は下層トランザクションへ委譲する。
    // ユーザは IGraphTransaction 経由でフックを登録できる。
    public void OnCommitted(Action callback)
    {
        using var usage = EnterUsage();
        _inner.OnCommitted(callback);
    }

    public void OnRolledBack(Action callback)
    {
        using var usage = EnterUsage();
        _inner.OnRolledBack(callback);
    }
}

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
    private readonly ITokenStore<EdgeTypeId> _edgeTypeTokens;
    private readonly PropertyKeyTokenStore _propKeyTokens;
    // null でない場合、公開ミューテーションをすべてバッファし、下層トランザクションが
    // 永続化コミットされた後にバッチをシンクへ引き渡す。
    private readonly ITokenStore<NexusTypeId> _nexusTypeTokens;
    private readonly ITokenStore<RoleId> _roleTokens;
    private readonly ILogicalMutationSink? _logicalSink;
    private List<LogicalMutation>? _logicalBuffer;
    private readonly Storage.Records.ColumnManager? _columns;
    private readonly Core.IVectorStore? _vectors;

    internal GraphTransaction(
        ITransaction inner,
        ITokenStore<LabelId> labelTokens,
        ITokenStore<EdgeTypeId> edgeTypeTokens,
        PropertyKeyTokenStore propKeyTokens,
        ITokenStore<NexusTypeId> nexusTypeTokens,
        ITokenStore<RoleId> roleTokens,
        bool isReadOnly = false,
        ILogicalMutationSink? logicalSink = null,
        Storage.Records.ColumnManager? columns = null,
        Core.IVectorStore? vectors = null)
    {
        _inner = inner;
        _labelTokens = labelTokens;
        _edgeTypeTokens = edgeTypeTokens;
        _propKeyTokens = propKeyTokens;
        _nexusTypeTokens = nexusTypeTokens;
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

    // MigrationContext.ForEachVertex が Access.ScanVertices に渡す。
    internal ITransaction Inner => _inner;

    private TransactionUsageLease EnterUsage() => _inner.EnterUsage();

    // ========== Vertex操作 ==========

    public VertexId CreateVertex(string label)
    {
        using var usage = EnterUsage();
        var labelId = _labelTokens.GetOrCreate(label);
        var vertexId = _inner.Vertices.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateVertex(vertexId, label));
        return vertexId;
    }

    public VertexId CreateVertex(LabelId labelId)
    {
        using var usage = EnterUsage();
        var vertexId = _inner.Vertices.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateVertex(vertexId, _labelTokens.GetName(labelId)));
        return vertexId;
    }

    public void DeleteVertex(VertexId vertexId)
    {
        using var usage = EnterUsage();
        var firstEdgeId = _inner.Vertices.Read(vertexId).FirstEdgeId;
        var edgesToDelete = new List<EdgeId>();
        var edgeId = firstEdgeId;
        while (edgeId.IsValid)
        {
            edgesToDelete.Add(edgeId);
            var edge = _inner.Edges.Read(edgeId);
            edgeId = edge.Source.Sequence == vertexId.Sequence ? edge.SourceNext : edge.TargetNext;
        }
        foreach (var rid in edgesToDelete)
            DeleteEdge(rid);

        var heToDelete = new HashSet<long>();
        var incEnum = _inner.Incidences.EnumerateByVertex(
            vertexId, _inner.VertexIncidenceHeads, _inner.Nexuses);
        while (incEnum.MoveNext())
            heToDelete.Add(incEnum.Current.NexusId.Sequence);
        foreach (var heSeq in heToDelete)
            DeleteNexus(new NexusId(heSeq));

        if (_inner.Indexes.HasAnyFullTextIndex)
            RemoveVertexFromFullTextIndexes(vertexId);

        _inner.Vertices.Free(vertexId);
        _columns?.OnDeleteEntity(Core.EntityKind.Vertex, vertexId.Sequence, _inner.Id.Value);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteVertex(vertexId));
    }

    public bool VertexExists(VertexId vertexId)
    {
        using var usage = EnterUsage();
        return _inner.Vertices.Read(vertexId).InUse;
    }

    public string? GetVertexLabel(VertexId vertexId)
    {
        using var usage = EnterUsage();
        var vertex = _inner.Vertices.Read(vertexId);
        if (!vertex.InUse || !vertex.Label.IsValid) return null;
        return _labelTokens.GetName(vertex.Label);
    }

    public string? GetEdgeTypeName(EdgeTypeId typeId)
        => typeId.IsValid ? _edgeTypeTokens.GetName(typeId) : null;

    // ========== MERGE ==========

    // 「インデックス未登録」を初回 MergeVertex 呼び出し時に一度だけ警告する。
    // (label, propertyKey) 単位で重複抑制。プロセス共有で問題ない (誤検出より煩いログ抑制を優先)。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), byte>
        _mergeFullscanWarned = new();

    public (VertexId Id, bool Created) MergeVertex(string label, string matchKey, in PropertyValue matchValue)
    {
        using var usage = EnterUsage();
        var labelId = _labelTokens.GetOrCreate(label);

        // (label, matchKey) にインデックスが登録されていれば、
        // SeekVerticesByIndex で O(log n) シーク。なければ既存のフルスキャン経路へフォールバック。
        if (_inner.Indexes.TryGetIndexName(label, matchKey, out var indexName))
        {
            foreach (var vertexId in _inner.Access.SeekVerticesByIndex(_inner, indexName, matchValue))
            {
                // インデックスには削除済みVertexの古いエントリが残ることがあるので生存確認。
                if (_inner.Vertices.Read(vertexId).InUse)
                    return (vertexId, false);
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
                        "Quiver.MergeVertex: (label='{0}', key='{1}') にインデックスが未登録のためフルスキャンに落ちました。" +
                        " 'Schema.CreateIndex(name, label, propertyKey, kind)' で索引を作成すると O(log n) になります。",
                        label, matchKey);
                }
                foreach (var vertexId in _inner.Access.ScanVertices(_inner, labelId))
                {
                    // inline + overflow を結合列挙 (inline のみのVertexも拾う)。
                    var propEnum = _inner.Vertices.EnumerateProperties(vertexId, _inner.Properties);
                    while (propEnum.MoveNext())
                    {
                        var cur = propEnum.Current;
                        if (cur.KeyId != keyId) continue;
                        if (PropertyValueEqualityHelper.AreEqual(cur.Value, in matchValue))
                            return (vertexId, false);
                        break;
                    }
                }
            }
        }

        var newId = _inner.Vertices.Allocate(labelId);
        var newKeyId = _propKeyTokens.GetOrCreate(matchKey);
        SetVertexProperty(newId, newKeyId, in matchValue);
        // インデックスが登録されていれば新規エントリも追加する。
        // これが無いと「初回 MergeVertex は遅い、2 回目以降の MergeVertex で同じキーを見つけられない」
        // 状態になり upsert セマンティクスが壊れる。
        if (!string.IsNullOrEmpty(indexName))
            InsertIntoIndex(indexName, in matchValue, newId);
        return (newId, true);
    }

    private void InsertIntoIndex(string indexName, in PropertyValue value, VertexId vertexId)
    {
        long packed = PackVertex(vertexId);
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

    private void RemoveFromIndex(string indexName, in PropertyValue value, VertexId vertexId)
    {
        long packed = PackVertex(vertexId);
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

    private bool TryResolveSecondaryIndex(VertexId vertexId, string key, out string indexName)
    {
        var vertex = _inner.Vertices.Read(vertexId);
        if (!vertex.InUse || !vertex.Label.IsValid) { indexName = string.Empty; return false; }
        var labelName = _labelTokens.GetName(vertex.Label);
        if (string.IsNullOrEmpty(labelName)) { indexName = string.Empty; return false; }
        return _inner.Indexes.TryGetIndexName(labelName, key, out indexName);
    }

    // 索引の値レーンに (Kind=Vertex, Sequence=vertexId, Generation=現世代) をパックする。
    // 解決時に現 slot 世代と照合して slot 再利用 (ABA) の stale 参照を弾けるようにする。
    private long PackVertex(VertexId vertexId)
        => EntityRef.Pack(EntityKind.Vertex, vertexId.Sequence, _inner.Vertices.CurrentGeneration(vertexId.Sequence));

    // Vertexの (label, key) に bound された全文索引を引く。FT 索引がゼロなら fast-path で null。
    private FullTextIndex? ResolveFullTextIndex(VertexId vertexId, string key)
    {
        if (!_inner.Indexes.HasAnyFullTextIndex) return null;
        var vertex = _inner.Vertices.Read(vertexId);
        if (!vertex.InUse || !vertex.Label.IsValid) return null;
        var labelName = _labelTokens.GetName(vertex.Label);
        if (string.IsNullOrEmpty(labelName)) return null;
        return _inner.Indexes.TryGetFullTextIndexByLabelKey(labelName, key, out var ft) ? ft : null;
    }

    // Vertexの現在の string プロパティ値 (before-image) を読む。非 string / 未設定なら null。
    private string? ReadVertexStringProperty(VertexId vertexId, PropertyKeyId keyId)
    {
        var e = _inner.Vertices.EnumerateProperties(vertexId, _inner.Properties);
        while (e.MoveNext())
        {
            if (e.Current.KeyId != keyId) continue;
            var v = e.Current.Value;
            return v.Type == PropertyValueType.String
                ? System.Text.Encoding.UTF8.GetString(v.Utf8StringValue) : null;
        }
        return null;
    }

    // Vertex削除時に、その string プロパティを bound 全文索引から除去する。
    private void RemoveVertexFromFullTextIndexes(VertexId vertexId)
    {
        var vertex = _inner.Vertices.Read(vertexId);
        if (!vertex.InUse || !vertex.Label.IsValid) return;
        var labelName = _labelTokens.GetName(vertex.Label);
        if (string.IsNullOrEmpty(labelName)) return;
        long entityId = PackVertex(vertexId);
        // span は MoveNext で無効化されるため、文字列を先に materialize してから除去する。
        var docs = new List<(FullTextIndex Ft, string Text)>();
        var e = _inner.Vertices.EnumerateProperties(vertexId, _inner.Properties);
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

    public EdgeId CreateEdge(VertexId source, VertexId target, string type)
    {
        using var usage = EnterUsage();
        var typeId = _edgeTypeTokens.GetOrCreate(type);
        var edgeId = _inner.Edges.Create(_inner.Vertices, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateEdge(edgeId, source, target, type));
        return edgeId;
    }

    public EdgeId CreateEdge(VertexId source, VertexId target, EdgeTypeId typeId)
    {
        using var usage = EnterUsage();
        var edgeId = _inner.Edges.Create(_inner.Vertices, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateEdge(
                edgeId, source, target, _edgeTypeTokens.GetName(typeId)));
        return edgeId;
    }

    public (EdgeId Id, bool Created) MergeEdge(VertexId source, VertexId target, string type)
    {
        using var usage = EnterUsage();
        // 型トークンが未観測なら、その型のエッジは存在し得ない → 走査せず直接作成。
        // (公開 EnumerateEdges は型未知のとき全隣接へフォールバックするため、ここでは
        // store の typed + Outgoing 列挙を直接使い、別型エッジを target 一致で誤マッチしないようにする。)
        if (_edgeTypeTokens.TryGet(type, out var typeId))
        {
            var e = _inner.Edges.EnumerateNeighbors(source, _inner.Vertices, typeId, Direction.Outgoing);
            while (e.MoveNext())
            {
                // Outgoing 列挙では Current.Source == source が保証されるので Target だけ照合する。
                if (e.Current.Target == target)
                    return (e.Current.Id, false);
            }
        }
        return (CreateEdge(source, target, type), true);
    }

    public void DeleteEdge(EdgeId edgeId)
    {
        using var usage = EnterUsage();
        if (!_inner.Edges.Read(edgeId).InUse)
            return;
        FreeEdgeProperties(edgeId);
        // この ID が不変ベースビューに含まれる場合、隣接ブロックには依然として
        // 現れる — 後続の expand カーソルがスキップできるよう tombstone を記録する。
        // delta 側 ID に対してはストアは no-op。
        _inner.AdjacencyBlocks?.Tombstone(edgeId);
        _inner.Edges.Delete(_inner.Vertices, edgeId);
        // リレーション削除に伴い、その kind の全列で seq を論理削除する。
        _columns?.OnDeleteEntity(Core.EntityKind.Edge, edgeId.Sequence, _inner.Id.Value);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteEdge(edgeId));
    }

    private void FreeEdgeProperties(EdgeId edgeId)
    {
        var firstPropId = _inner.Edges.Read(edgeId).FirstPropertyId;
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

    public void SetProperty(VertexId vertexId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key);
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use AddPropertyValue for Set-cardinality property '{key}'.");
        // 透過維持: この (label, key) に全文索引が bound されているときだけ before-image を読む。
        // 非索引キーの書き込みは HasAnyFullTextIndex の bool チェックのみで素通り。
        var ft = ResolveFullTextIndex(vertexId, key);
        string? oldText = ft is null ? null : ReadVertexStringProperty(vertexId, keyId);
        // SetVertexProperty がチェーンを変更する前にキャプチャする — value は
        // ref struct のため、ヒープコピーは LogicalPropertyValue に閉じ込める。
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetVertexProperty(vertexId, key, in captured));
        }
        SetVertexProperty(vertexId, keyId, in value);
        // 列化済み key なら同 tx で列を維持する。
        _columns?.OnSetProperty(Core.EntityKind.Vertex, vertexId.Sequence, keyId, in value, _inner.Id.Value);
        if (ft is not null)
        {
            string? newText = value.Type == PropertyValueType.String
                ? System.Text.Encoding.UTF8.GetString(value.Utf8StringValue) : null;
            if (oldText is not null || newText is not null)
                _inner.Indexes.MaintainFullText(ft, PackVertex(vertexId), oldText, newText);
        }
    }

    public void SetProperty(EdgeId edgeId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key);
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use AddPropertyValue for Set-cardinality property '{key}'.");
        if (!_inner.Edges.Read(edgeId).InUse)
            return;
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetEdgeProperty(edgeId, key, in captured));
        }
        SetEdgeProperty(edgeId, keyId, in value);
        // 列化済み key なら同 tx で列を維持する。
        _columns?.OnSetProperty(Core.EntityKind.Edge, edgeId.Sequence, keyId, in value, _inner.Id.Value);
    }

    private void SetEdgeProperty(EdgeId edgeId, PropertyKeyId keyId, in PropertyValue value)
    {
        // 小さい値は edge record へ inline (copy-on-write)。
        if (InlinePropertyCodec.IsInlineable(value) && _inner.Edges.SetInlineProperty(edgeId, keyId, in value))
        {
            // size-class 変更で同 key が overflow に残っていれば除去する。
            RemoveEdgeOverflowIfPresent(edgeId, keyId);
            return;
        }
        // inline 不可 / 予算超過 → overflow チェーン。inline 側に旧値があれば除去。
        _inner.Edges.RemoveInlineProperty(edgeId, keyId);
        SetEdgeOverflow(edgeId, keyId, in value);
    }

    private void SetEdgeOverflow(EdgeId edgeId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Edges.Read(edgeId).FirstPropertyId;
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
        var wh = _inner.Edges.Write(edgeId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    private void RemoveEdgeOverflowIfPresent(EdgeId edgeId, PropertyKeyId keyId)
    {
        var firstPropId = _inner.Edges.Read(edgeId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Edges.Write(edgeId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    private void SetVertexProperty(VertexId vertexId, PropertyKeyId keyId, in PropertyValue value)
    {
        // 小さい値は vertex record へ inline (copy-on-write)。
        if (InlinePropertyCodec.IsInlineable(value) && _inner.Vertices.SetInlineProperty(vertexId, keyId, in value))
        {
            // size-class 変更で同 key が overflow に残っていれば除去する。
            RemoveVertexOverflowIfPresent(vertexId, keyId);
            return;
        }
        // inline 不可 / 予算超過 → overflow チェーン。inline 側に旧値があれば除去。
        _inner.Vertices.RemoveInlineProperty(vertexId, keyId);
        SetVertexOverflow(vertexId, keyId, in value);
    }

    private void SetVertexOverflow(VertexId vertexId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Vertices.Read(vertexId).FirstPropertyId;
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
        var wh = _inner.Vertices.Write(vertexId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    private void RemoveVertexOverflowIfPresent(VertexId vertexId, PropertyKeyId keyId)
    {
        var firstPropId = _inner.Vertices.Read(vertexId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Vertices.Write(vertexId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    public void RemoveProperty(VertexId vertexId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;

        // inline を先に試し、無ければ overflow チェーンから除去。
        bool removed = _inner.Vertices.RemoveInlineProperty(vertexId, keyId);
        if (!removed)
        {
            var firstPropId = _inner.Vertices.Read(vertexId).FirstPropertyId;
            var propEnum = _inner.Properties.Enumerate(firstPropId);
            while (propEnum.MoveNext())
            {
                if (propEnum.Current.KeyId == keyId)
                {
                    var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                    var wh = _inner.Vertices.Write(vertexId);
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
            _columns?.OnRemoveProperty(Core.EntityKind.Vertex, vertexId.Sequence, keyId, _inner.Id.Value);
            if (_logicalSink != null)
                RecordLogical(LogicalMutation.RemoveVertexProperty(vertexId, key));
        }
    }

    public PropertyValue GetProperty(VertexId vertexId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use GetPropertyValues for Set-cardinality property '{key}'.");
        // inline を先に引き、無ければ overflow チェーンを walk。
        if (_inner.Vertices.TryGetInlineProperty(vertexId, keyId, out var inlineVal)) return inlineVal;
        var firstPropId = _inner.Vertices.Read(vertexId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId) return prop.Value;
        }
        return default;
    }

    public PropertyValue GetProperty(EdgeId edgeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use GetPropertyValues for Set-cardinality property '{key}'.");
        // inline を先に引き、無ければ overflow チェーンを walk。
        if (_inner.Edges.TryGetInlineProperty(edgeId, keyId, out var inlineVal)) return inlineVal;
        var firstPropId = _inner.Edges.Read(edgeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId) return prop.Value;
        }
        return default;
    }

    public bool HasProperty(VertexId vertexId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        if (_inner.Vertices.HasInlineProperty(vertexId, keyId)) return true;
        var firstPropId = _inner.Vertices.Read(vertexId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId) return true;
        }
        return false;
    }

    public PropertyEnumerator EnumerateProperties(VertexId vertexId)
    {
        using var usage = EnterUsage();
        // inline (visible 版) + overflow チェーンを結合して列挙。
        return _inner.Vertices.EnumerateProperties(vertexId, _inner.Properties);
    }

    // ========== マルチバリュープロパティ操作 (Set cardinality) ==========

    public void AddPropertyValue(VertexId vertexId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key, PropertyCardinality.Set);

        // 重複チェック: 同一 key+value の visible エントリがあればスキップ (Set セマンティクス)
        var propEnum = _inner.Vertices.EnumerateProperties(vertexId, _inner.Properties);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
                return;
        }

        // overflow チェーンの head に prepend (既存 same-key エントリは削除しない)
        var firstPropId = _inner.Vertices.Read(vertexId).FirstPropertyId;
        var newPropId = _inner.Properties.Create(keyId, in value, firstPropId);
        var wh = _inner.Vertices.Write(vertexId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();

        if (TryResolveSecondaryIndex(vertexId, key, out var indexName))
            InsertIntoIndex(indexName, in value, vertexId);
    }

    public void AddPropertyValue(EdgeId edgeId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key, PropertyCardinality.Set);
        if (!_inner.Edges.Read(edgeId).InUse)
            return;

        var propEnum = _inner.Edges.EnumerateProperties(edgeId, _inner.Properties);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
                return;
        }

        var firstPropId = _inner.Edges.Read(edgeId).FirstPropertyId;
        var newPropId = _inner.Properties.Create(keyId, in value, firstPropId);
        var wh = _inner.Edges.Write(edgeId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    public void RemovePropertyValue(VertexId vertexId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");

        var firstPropId = _inner.Vertices.Read(vertexId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && propEnum.Current.InUse
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Vertices.Write(vertexId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();

                if (TryResolveSecondaryIndex(vertexId, key, out var indexName))
                    RemoveFromIndex(indexName, in value, vertexId);
                return;
            }
        }
    }

    public void RemovePropertyValue(EdgeId edgeId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");
        if (!_inner.Edges.Read(edgeId).InUse)
            return;

        var firstPropId = _inner.Edges.Read(edgeId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && propEnum.Current.InUse
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Edges.Write(edgeId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    public PropertyValuesEnumerator GetPropertyValues(VertexId vertexId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
            return new PropertyValuesEnumerator(
                new PropertyEnumerator(null!, PropertyId.Invalid), default);
        return new PropertyValuesEnumerator(
            _inner.Vertices.EnumerateProperties(vertexId, _inner.Properties), keyId);
    }

    public PropertyValuesEnumerator GetPropertyValues(EdgeId edgeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
            return new PropertyValuesEnumerator(
                new PropertyEnumerator(null!, PropertyId.Invalid), default);
        return new PropertyValuesEnumerator(
            _inner.Edges.EnumerateProperties(edgeId, _inner.Properties), keyId);
    }

    // ========== トラバーサル ==========

    public EdgeEnumerator EnumerateEdges(
        VertexId vertexId,
        Direction direction = Direction.Both,
        string? typeFilter = null)
    {
        using var usage = EnterUsage();
        if (typeFilter != null && _edgeTypeTokens.TryGet(typeFilter, out var typeId))
            return _inner.Edges.EnumerateNeighbors(vertexId, _inner.Vertices, typeId, direction);

        return _inner.Edges.EnumerateNeighbors(vertexId, _inner.Vertices);
    }

    // ========== インデックス ==========

    public void IndexInsert(string indexName, string key, VertexId vertexId)
    {
        using var usage = EnterUsage();
        _inner.Indexes.CreateStringIndex(indexName).Insert(key, PackVertex(vertexId));
    }

    public void IndexInsert(string indexName, long key, VertexId vertexId)
    {
        using var usage = EnterUsage();
        _inner.Indexes.CreateInt64Index(indexName).Insert(key, PackVertex(vertexId));
    }

    public void IndexInsert(string indexName, double key, VertexId vertexId)
    {
        using var usage = EnterUsage();
        _inner.Indexes.CreateDoubleIndex(indexName).Insert(key, PackVertex(vertexId));
    }

    public VertexIdEnumerator SeekIndex(string indexName, in PropertyValue key)
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
        // パック値を世代照合しつつ VertexId.Value へ unpack する。
        return new VertexIdEnumerator(IndexValueResolver.ResolveLiveVertexSequences(values, _inner.Vertices));
    }

    public VertexIdEnumerator RangeIndex(
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
        // パック値を世代照合しつつ VertexId.Value へ unpack する。
        return new VertexIdEnumerator(IndexValueResolver.ResolveLiveVertexSequences(values, _inner.Vertices));
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
            // 結果 VertexId 列に現世代を load (round-trip 一貫)。
            QueryRowMaterializer.StampEntityGenerations(
                slots, _inner.Vertices, _inner.Edges, _inner.Nexuses);
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
            plan, _inner.Vertices, _inner.Edges, _inner.Nexuses);
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

    // ========== ベクトル (tx 配下) ==========

    public void SetVector(Core.EntityKind kind, long entityId, string indexName, ReadOnlySpan<float> vector)
    {
        using var usage = EnterUsage();
        if (IsReadOnly)
            throw new InvalidOperationException("Cannot SetVector in a read-only transaction.");
        if (_vectors is null)
            throw new NotSupportedException("This backend does not support transaction-scoped SetVector.");
        // tx の ambient WalWriteSetContext 下で書く → グラフ変更と同じ WAL に乗り、
        // commit で原子確定し、プロセス内 abort は before-image で巻き戻る。
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

    // ========== Nexus操作 ==========

    public NexusId CreateNexus(string type, ReadOnlySpan<NexusMember> members)
    {
        using var usage = EnterUsage();
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Nexus type must not be null, empty, or whitespace.", nameof(type));
        var typeId = _nexusTypeTokens.GetOrCreate(type);
        return CreateNexusCore(typeId, members);
    }

    public NexusId CreateNexus(NexusTypeId typeId, ReadOnlySpan<NexusMember> members)
    {
        using var usage = EnterUsage();
        if (!typeId.IsValid)
            throw new ArgumentException("Invalid nexus type ID.", nameof(typeId));
        return CreateNexusCore(typeId, members);
    }

    private NexusId CreateNexusCore(NexusTypeId typeId, ReadOnlySpan<NexusMember> members)
    {
        if (members.Length < 2)
            throw new ArgumentException("A nexus requires at least 2 members.", nameof(members));

        Span<IncidenceMember> resolved = members.Length <= 16
            ? stackalloc IncidenceMember[members.Length]
            : new IncidenceMember[members.Length];

        var seen = new HashSet<(int, long)>();
        for (int i = 0; i < members.Length; i++)
        {
            var m = members[i];
            if (string.IsNullOrWhiteSpace(m.Role))
                throw new ArgumentException($"Member role at index {i} must not be null, empty, or whitespace.", nameof(members));

            var vertex = _inner.Vertices.Read(m.VertexId);
            if (!vertex.InUse)
                throw new ArgumentException($"Vertex {m.VertexId} at index {i} does not exist or is not visible.", nameof(members));

            var roleId = _roleTokens.GetOrCreate(m.Role);
            if (!seen.Add((roleId.Value, m.VertexId.Sequence)))
                throw new ArgumentException($"Duplicate (Role, VertexId) pair at index {i}: ({m.Role}, {m.VertexId}).", nameof(members));

            resolved[i] = new IncidenceMember(m.VertexId, roleId);
        }

        var nexusId = _inner.Nexuses.Create(typeId, resolved, _inner.Incidences, _inner.VertexIncidenceHeads);

        // 論理ストリームは自己完結させる — メンバーは型名とロール名 (トークン ID ではなく)
        // で保持し、別 DB への再生時にターゲット側の ID へ再マッピングできるようにする。
        if (_logicalSink != null)
        {
            var typeName = _nexusTypeTokens.GetName(typeId);
            var captured = new NexusMember[members.Length];
            for (int i = 0; i < members.Length; i++)
                captured[i] = members[i];
            RecordLogical(LogicalMutation.CreateNexus(nexusId, typeName, captured));
        }
        return nexusId;
    }

    public void DeleteNexus(NexusId nexusId)
    {
        using var usage = EnterUsage();
        // header の可視性が incidence とプロパティの可視性の正本 — header を論理削除すれば
        // それらも同スナップショットで不可視になる。overflow プロパティレコードは物理的に残る
        // ため、slot を回収できるよう先にチェーンを解放してから header をスタンプする。
        FreeNexusProperties(nexusId);
        _inner.Nexuses.Delete(nexusId);
        _columns?.OnDeleteEntity(Core.EntityKind.Nexus, nexusId.Sequence, _inner.Id.Value);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteNexus(nexusId));
    }

    private void FreeNexusProperties(NexusId nexusId)
    {
        var firstPropId = _inner.Nexuses.Read(nexusId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var toDelete = new List<PropertyId>();
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
            toDelete.Add(propEnum.Current.Id);
        var currentFirst = firstPropId;
        foreach (var pid in toDelete)
            currentFirst = _inner.Properties.Delete(pid, currentFirst);
    }

    public NexusMemberEnumerator GetMembers(NexusId nexusId, string? role = null)
    {
        using var usage = EnterUsage();
        RoleId roleFilter = RoleId.Invalid;
        if (role != null && _roleTokens.TryGet(role, out var rid))
            roleFilter = rid;
        else if (role != null)
            return default;

        var innerEnum = _inner.Incidences.EnumerateByNexus(nexusId, _inner.Nexuses);
        return new NexusMemberEnumerator(innerEnum, _roleTokens, _inner.Vertices, roleFilter);
    }

    public NexusIdEnumerator GetNexuses(VertexId vertexId, string? type = null, string? role = null)
    {
        using var usage = EnterUsage();
        NexusTypeId typeFilter = NexusTypeId.Invalid;
        if (type != null && _nexusTypeTokens.TryGet(type, out var tid))
            typeFilter = tid;
        else if (type != null)
            return default;

        RoleId roleFilter = RoleId.Invalid;
        if (role != null && _roleTokens.TryGet(role, out var rid))
            roleFilter = rid;
        else if (role != null)
            return default;

        var innerEnum = _inner.Incidences.EnumerateByVertex(
            vertexId, _inner.VertexIncidenceHeads, _inner.Nexuses);
        return new NexusIdEnumerator(innerEnum, _inner.Nexuses, typeFilter, roleFilter);
    }

    public string? GetNexusTypeName(NexusTypeId typeId)
        => typeId.IsValid ? _nexusTypeTokens.GetName(typeId) : null;

    // ========== Nexusプロパティ操作 ==========
    // vertex / edge と同じプロパティエンティティとして扱う。header 15 バイト固定領域の後ろへ
    // inline 符号化し、収まらない値は既存 PropertyStore の overflow チェーンへ回す。

    public void SetProperty(NexusId nexusId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key);
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use AddPropertyValue for Set-cardinality property '{key}'.");
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetNexusProperty(nexusId, key, in captured));
        }
        SetNexusProperty(nexusId, keyId, in value);
        // 列化済み key なら同 tx で列を維持する (列作成の公開糖衣は後続タスク)。
        _columns?.OnSetProperty(Core.EntityKind.Nexus, nexusId.Sequence, keyId, in value, _inner.Id.Value);
    }

    private void SetNexusProperty(NexusId nexusId, PropertyKeyId keyId, in PropertyValue value)
    {
        // 小さい値は header へ inline (copy-on-write)。
        if (InlinePropertyCodec.IsInlineable(value) && _inner.Nexuses.SetInlineProperty(nexusId, keyId, in value))
        {
            // size-class 変更で同 key が overflow に残っていれば除去する。
            RemoveNexusOverflowIfPresent(nexusId, keyId);
            return;
        }
        // inline 不可 / 予算超過 → overflow チェーン。inline 側に旧値があれば除去。
        _inner.Nexuses.RemoveInlineProperty(nexusId, keyId);
        SetNexusOverflow(nexusId, keyId, in value);
    }

    private void SetNexusOverflow(NexusId nexusId, PropertyKeyId keyId, in PropertyValue value)
    {
        var firstPropId = _inner.Nexuses.Read(nexusId).FirstPropertyId;
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
        var wh = _inner.Nexuses.Write(nexusId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();
    }

    private void RemoveNexusOverflowIfPresent(NexusId nexusId, PropertyKeyId keyId)
    {
        var firstPropId = _inner.Nexuses.Read(nexusId).FirstPropertyId;
        if (!firstPropId.IsValid) return;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId)
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Nexuses.Write(nexusId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();
                return;
            }
        }
    }

    public PropertyValue GetProperty(NexusId nexusId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use GetPropertyValues for Set-cardinality property '{key}'.");
        // inline を先に引き、無ければ overflow チェーンを walk。
        if (_inner.Nexuses.TryGetInlineProperty(nexusId, keyId, out var inlineVal)) return inlineVal;
        var firstPropId = _inner.Nexuses.Read(nexusId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            var prop = propEnum.Current;
            if (prop.KeyId == keyId) return prop.Value;
        }
        return default;
    }

    public bool HasProperty(NexusId nexusId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        if (_inner.Nexuses.HasInlineProperty(nexusId, keyId)) return true;
        var firstPropId = _inner.Nexuses.Read(nexusId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId) return true;
        }
        return false;
    }

    public void RemoveProperty(NexusId nexusId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;

        // inline を先に試し、無ければ overflow チェーンから除去。
        bool removed = _inner.Nexuses.RemoveInlineProperty(nexusId, keyId);
        if (!removed)
        {
            var firstPropId = _inner.Nexuses.Read(nexusId).FirstPropertyId;
            var propEnum = _inner.Properties.Enumerate(firstPropId);
            while (propEnum.MoveNext())
            {
                if (propEnum.Current.KeyId == keyId)
                {
                    var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                    var wh = _inner.Nexuses.Write(nexusId);
                    wh.FirstPropertyId = newFirst;
                    wh.Dispose();
                    removed = true;
                    break;
                }
            }
        }
        if (removed)
        {
            _columns?.OnRemoveProperty(Core.EntityKind.Nexus, nexusId.Sequence, keyId, _inner.Id.Value);
            if (_logicalSink != null)
                RecordLogical(LogicalMutation.RemoveNexusProperty(nexusId, key));
        }
    }

    public PropertyEnumerator EnumerateProperties(NexusId nexusId)
    {
        using var usage = EnterUsage();
        return _inner.Nexuses.EnumerateProperties(nexusId, _inner.Properties);
    }

    // ── Nexusのマルチバリュープロパティ (Set cardinality) ──

    public void AddPropertyValue(NexusId nexusId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        var keyId = _propKeyTokens.GetOrCreate(key, PropertyCardinality.Set);

        // 重複チェック: 同一 key+value の visible エントリがあればスキップ (Set セマンティクス)。
        var scan = _inner.Nexuses.EnumerateProperties(nexusId, _inner.Properties);
        while (scan.MoveNext())
        {
            if (scan.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(scan.Current.Value, in value))
                return;
        }

        // overflow チェーンの head に prepend (既存 same-key エントリは削除しない)。
        var firstPropId = _inner.Nexuses.Read(nexusId).FirstPropertyId;
        var newPropId = _inner.Properties.Create(keyId, in value, firstPropId);
        var wh = _inner.Nexuses.Write(nexusId);
        wh.FirstPropertyId = newPropId;
        wh.Dispose();

        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.AddNexusPropertyValue(nexusId, key, in captured));
        }
    }

    public void RemovePropertyValue(NexusId nexusId, string key, in PropertyValue value)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");

        var firstPropId = _inner.Nexuses.Read(nexusId).FirstPropertyId;
        var propEnum = _inner.Properties.Enumerate(firstPropId);
        while (propEnum.MoveNext())
        {
            if (propEnum.Current.KeyId == keyId
                && propEnum.Current.InUse
                && PropertyValueEqualityHelper.AreEqual(propEnum.Current.Value, in value))
            {
                var newFirst = _inner.Properties.Delete(propEnum.Current.Id, firstPropId);
                var wh = _inner.Nexuses.Write(nexusId);
                wh.FirstPropertyId = newFirst;
                wh.Dispose();

                if (_logicalSink != null)
                {
                    var captured = LogicalPropertyValue.Capture(in value);
                    RecordLogical(LogicalMutation.RemoveNexusPropertyValue(nexusId, key, in captured));
                }
                return;
            }
        }
    }

    public PropertyValuesEnumerator GetPropertyValues(NexusId nexusId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
            return new PropertyValuesEnumerator(
                new PropertyEnumerator(null!, PropertyId.Invalid), default);
        return new PropertyValuesEnumerator(
            _inner.Nexuses.EnumerateProperties(nexusId, _inner.Properties), keyId);
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

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
    public TransactionId TransactionId => _inner.Id;

    public TransactionUsageLease EnterUsage() => _inner.EnterUsage();

    // ========== Vertex操作 ==========

    public VertexId CreateVertex(string label)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        var labelId = _labelTokens.GetOrCreate(label);
        var vertexId = _inner.Vertices.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateVertex(vertexId, label));
        return vertexId;
    }

    public VertexId CreateVertex(LabelId labelId)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        var vertexId = _inner.Vertices.Allocate(labelId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateVertex(vertexId, _labelTokens.GetName(labelId)));
        return vertexId;
    }

    public void DeleteVertex(VertexId vertexId)
    {
        EnsureWritable();
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
            DeleteEdgeCore(rid);

        var heToDelete = new HashSet<long>();
        var incEnum = _inner.Incidences.EnumerateByVertex(
            vertexId, _inner.VertexIncidenceHeads, _inner.Nexuses);
        while (incEnum.MoveNext())
            heToDelete.Add(incEnum.Current.NexusId.Sequence);
        foreach (var heSeq in heToDelete)
            DeleteNexusCore(new NexusId(heSeq));

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
        EnsureWritable();
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
        EnsureWritable();
        using var usage = EnterUsage();
        var typeId = _edgeTypeTokens.GetOrCreate(type);
        return CreateEdgeCore(source, target, typeId, type);
    }

    public EdgeId CreateEdge(VertexId source, VertexId target, EdgeTypeId typeId)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        return CreateEdgeCore(source, target, typeId, _edgeTypeTokens.GetName(typeId));
    }

    private EdgeId CreateEdgeCore(
        VertexId source,
        VertexId target,
        EdgeTypeId typeId,
        string typeName)
    {
        var edgeId = _inner.Edges.Create(_inner.Vertices, source, target, typeId);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.CreateEdge(edgeId, source, target, typeName));
        return edgeId;
    }

    public (EdgeId Id, bool Created) MergeEdge(VertexId source, VertexId target, string type)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        // Merge の作成パスでは型トークンが必ず必要になるため、先に同一 ID へ解決する。
        // その ID で既存 adjacency を照合すれば、同一 tx で新規作成した型も確実に検索できる。
        var typeId = _edgeTypeTokens.GetOrCreate(type);
        var e = _inner.Edges.EnumerateNeighbors(source, _inner.Vertices);
        while (e.MoveNext())
        {
            if (e.Current.Source.Sequence == source.Sequence
                && e.Current.Target.Sequence == target.Sequence
                && e.Current.Type == typeId)
                return (e.Current.Id, false);
        }
        return (CreateEdgeCore(source, target, typeId, type), true);
    }

    public void DeleteEdge(EdgeId edgeId)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        DeleteEdgeCore(edgeId);
    }

    private void DeleteEdgeCore(EdgeId edgeId)
    {
        if (!_inner.Edges.Read(edgeId).InUse)
            return;
        FreeEdgeProperties(edgeId);
        // この ID が不変ベースビューに含まれる場合、隣接ブロックには依然として
        // 現れる — 後続の expand カーソルがスキップできるよう tombstone を記録する。
        // delta 側 ID に対してはストアは no-op。
        _inner.AdjacencySegments?.Tombstone(edgeId);
        _inner.Edges.Delete(_inner.Vertices, edgeId);
        // リレーション削除に伴い、その kind の全列で seq を論理削除する。
        _columns?.OnDeleteEntity(Core.EntityKind.Edge, edgeId.Sequence, _inner.Id.Value);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.DeleteEdge(edgeId));
    }

    private void FreeEdgeProperties(EdgeId edgeId)
    {
        EntityRef owner = PropertyOwner(edgeId);
        var firstProperty = _inner.Edges.Read(edgeId).FirstPropertyRef;
        if (!firstProperty.IsValid) return;
        var toDelete = new List<PropertyVersionRef>();
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
            toDelete.Add(cursor.CurrentVersion);
        foreach (var version in toDelete)
            _inner.Properties.Delete(owner, version, firstProperty);
    }

    // ========== プロパティ操作 ==========

    public void SetProperty(VertexId vertexId, string key, in PropertyValue value)
    {
        EnsureWritable();
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
        EnsureWritable();
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
        EntityRef owner = PropertyOwner(edgeId);
        var firstProperty = _inner.Edges.Read(edgeId).FirstPropertyRef;
        var newHead = SetSingleProperty(owner, firstProperty, keyId, in value);
        var wh = _inner.Edges.Write(edgeId);
        wh.FirstPropertyRef = newHead;
        wh.Dispose();
    }

    private void SetVertexProperty(VertexId vertexId, PropertyKeyId keyId, in PropertyValue value)
    {
        EntityRef owner = PropertyOwner(vertexId);
        // property chain を変更してから owner を lock する順序では、ラッパーが保証する owner lock の
        // 外側で version が追加される。write handle を先に取得し、同じ pin で head の読書きを完結させる。
        var wh = _inner.Vertices.Write(vertexId);
        try
        {
            var newHead = SetSingleProperty(owner, wh.FirstPropertyRef, keyId, in value);
            wh.FirstPropertyRef = newHead;
        }
        finally
        {
            wh.Dispose();
        }
    }

    private PropertyVersionRef SetSingleProperty(
        EntityRef owner,
        PropertyVersionRef firstProperty,
        PropertyKeyId keyId,
        in PropertyValue value)
    {
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
        {
            if (cursor.Current.KeyId == keyId)
            {
                _inner.Properties.Delete(owner, cursor.CurrentVersion, firstProperty);
                break;
            }
        }
        var address = new PropertyAddress(owner, keyId);
        return _inner.Properties.Create(address, PropertyCardinality.Single, in value, firstProperty);
    }

    // Generation 0 の typed ID は物理アドレスとして受け取る既存経路がある。
    // property owner を永続化する境界では current generation を補い、別 incarnation への alias を防ぐ。
    private EntityRef PropertyOwner(VertexId id)
    {
        int generation = id.Generation == 0 ? _inner.Vertices.CurrentGeneration(id.Sequence) : id.Generation;
        return EntityRef.From(generation > 0 ? VertexId.Create(id.Sequence, generation) : id);
    }

    private EntityRef PropertyOwner(EdgeId id)
    {
        int generation = id.Generation == 0 ? _inner.Edges.CurrentGeneration(id.Sequence) : id.Generation;
        return EntityRef.From(generation > 0 ? EdgeId.Create(id.Sequence, generation) : id);
    }

    private EntityRef PropertyOwner(NexusId id)
    {
        int generation = id.Generation == 0 ? _inner.Nexuses.CurrentGeneration(id.Sequence) : id.Generation;
        return EntityRef.From(generation > 0 ? NexusId.Create(id.Sequence, generation) : id);
    }

    private bool RemovePropertyCore(EntityRef owner, PropertyVersionRef firstProperty, PropertyKeyId keyId)
    {
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
        {
            if (cursor.Current.KeyId != keyId) continue;
            _inner.Properties.Delete(owner, cursor.CurrentVersion, firstProperty);
            return true;
        }
        return false;
    }

    private PropertyValue GetPropertyCore(EntityRef owner, PropertyVersionRef firstProperty, PropertyKeyId keyId)
    {
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
        {
            if (cursor.Current.KeyId == keyId)
                return cursor.Current.Value;
        }
        return default;
    }

    private bool HasPropertyCore(EntityRef owner, PropertyVersionRef firstProperty, PropertyKeyId keyId)
    {
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
        {
            if (cursor.Current.KeyId == keyId)
                return true;
        }
        return false;
    }

    public void RemoveProperty(VertexId vertexId, string key)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;

        var firstProperty = _inner.Vertices.Read(vertexId).FirstPropertyRef;
        bool removed = RemovePropertyCore(PropertyOwner(vertexId), firstProperty, keyId);
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
        var firstProperty = _inner.Vertices.Read(vertexId).FirstPropertyRef;
        return GetPropertyCore(PropertyOwner(vertexId), firstProperty, keyId);
    }

    public PropertyValue GetProperty(EdgeId edgeId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use GetPropertyValues for Set-cardinality property '{key}'.");
        var firstProperty = _inner.Edges.Read(edgeId).FirstPropertyRef;
        return GetPropertyCore(PropertyOwner(edgeId), firstProperty, keyId);
    }

    public bool HasProperty(VertexId vertexId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        var firstProperty = _inner.Vertices.Read(vertexId).FirstPropertyRef;
        return HasPropertyCore(PropertyOwner(vertexId), firstProperty, keyId);
    }

    public PropertyCursor EnumerateProperties(VertexId vertexId)
    {
        var usage = EnterUsage();
        try
        {
            var cursor = _inner.Vertices.EnumerateProperties(vertexId, _inner.Properties);
            cursor.AttachUsage(usage);
            return cursor;
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    // ========== マルチバリュープロパティ操作 (Set cardinality) ==========

    public void AddPropertyValue(VertexId vertexId, string key, in PropertyValue value)
    {
        EnsureWritable();
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

        var firstProperty = _inner.Vertices.Read(vertexId).FirstPropertyRef;
        var newHead = _inner.Properties.Create(
            new PropertyAddress(PropertyOwner(vertexId), keyId),
            PropertyCardinality.Set,
            in value,
            firstProperty);
        var wh = _inner.Vertices.Write(vertexId);
        wh.FirstPropertyRef = newHead;
        wh.Dispose();

        if (TryResolveSecondaryIndex(vertexId, key, out var indexName))
            InsertIntoIndex(indexName, in value, vertexId);
    }

    public void AddPropertyValue(EdgeId edgeId, string key, in PropertyValue value)
    {
        EnsureWritable();
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

        var firstProperty = _inner.Edges.Read(edgeId).FirstPropertyRef;
        var newHead = _inner.Properties.Create(
            new PropertyAddress(PropertyOwner(edgeId), keyId),
            PropertyCardinality.Set,
            in value,
            firstProperty);
        var wh = _inner.Edges.Write(edgeId);
        wh.FirstPropertyRef = newHead;
        wh.Dispose();
    }

    public void RemovePropertyValue(VertexId vertexId, string key, in PropertyValue value)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");

        var firstProperty = _inner.Vertices.Read(vertexId).FirstPropertyRef;
        EntityRef owner = PropertyOwner(vertexId);
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
        {
            if (cursor.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(cursor.Current.Value, in value))
            {
                _inner.Properties.Delete(owner, cursor.CurrentVersion, firstProperty);

                if (TryResolveSecondaryIndex(vertexId, key, out var indexName))
                    RemoveFromIndex(indexName, in value, vertexId);
                return;
            }
        }
    }

    public void RemovePropertyValue(EdgeId edgeId, string key, in PropertyValue value)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");
        if (!_inner.Edges.Read(edgeId).InUse)
            return;

        var firstProperty = _inner.Edges.Read(edgeId).FirstPropertyRef;
        EntityRef owner = PropertyOwner(edgeId);
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
        {
            if (cursor.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(cursor.Current.Value, in value))
            {
                _inner.Properties.Delete(owner, cursor.CurrentVersion, firstProperty);
                return;
            }
        }
    }

    public PropertyValuesEnumerator GetPropertyValues(VertexId vertexId, string key)
    {
        var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
        {
            usage.Dispose();
            return new PropertyValuesEnumerator(
                new PropertyCursor(null!, default, PropertyVersionRef.Invalid), default);
        }
        try
        {
            var cursor = _inner.Vertices.EnumerateProperties(vertexId, _inner.Properties);
            cursor.AttachUsage(usage);
            return new PropertyValuesEnumerator(cursor, keyId);
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    public PropertyValuesEnumerator GetPropertyValues(EdgeId edgeId, string key)
    {
        var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
        {
            usage.Dispose();
            return new PropertyValuesEnumerator(
                new PropertyCursor(null!, default, PropertyVersionRef.Invalid), default);
        }
        try
        {
            var cursor = _inner.Edges.EnumerateProperties(edgeId, _inner.Properties);
            cursor.AttachUsage(usage);
            return new PropertyValuesEnumerator(cursor, keyId);
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    // ========== トラバーサル ==========

    public EdgeEnumerator EnumerateEdges(
        VertexId vertexId,
        Direction direction = Direction.Both,
        string? typeFilter = null)
    {
        var usage = EnterUsage();
        try
        {
            EdgeEnumerator enumerator;
            if (typeFilter != null && _edgeTypeTokens.TryGet(typeFilter, out var typeId))
                enumerator = _inner.Edges.EnumerateNeighbors(vertexId, _inner.Vertices, typeId, direction);
            else
                enumerator = _inner.Edges.EnumerateNeighbors(vertexId, _inner.Vertices);
            enumerator.AttachUsage(usage);
            return enumerator;
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    // ========== インデックス ==========

    public void IndexInsert(string indexName, string key, VertexId vertexId)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        _inner.Indexes.CreateStringIndex(indexName).Insert(key, PackVertex(vertexId));
    }

    public void IndexInsert(string indexName, long key, VertexId vertexId)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        _inner.Indexes.CreateInt64Index(indexName).Insert(key, PackVertex(vertexId));
    }

    public void IndexInsert(string indexName, double key, VertexId vertexId)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        _inner.Indexes.CreateDoubleIndex(indexName).Insert(key, PackVertex(vertexId));
    }

    public VertexIdEnumerator SeekIndex(string indexName, in PropertyValue key)
    {
        var usage = EnterUsage();
        try
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
            // パック値を世代照合しつつ VertexId.Value へ unpack する。
            return new VertexIdEnumerator(
                IndexValueResolver.ResolveLiveVertexSequences(values, _inner.Vertices),
                usage);
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    public VertexIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive)
    {
        var usage = EnterUsage();
        try
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
            // パック値を世代照合しつつ VertexId.Value へ unpack する。
            return new VertexIdEnumerator(
                IndexValueResolver.ResolveLiveVertexSequences(values, _inner.Vertices),
                usage);
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    // ========== 物理プラン実行 ==========

    public QueryResult Execute(IPhysicalOperator plan)
    {
        var usage = EnterUsage();
        try
        {
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
        finally
        {
            usage.Dispose();
        }
    }

    public IQueryCursor ExecuteCursor(IPhysicalOperator plan)
    {
        var usage = EnterUsage();
        try
        {
            plan.Open(_inner);
            return new PhysicalOperatorCursor(
                plan, _inner.Vertices, _inner.Edges, _inner.Nexuses, usage);
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    public IAdjacencySegmentStore? AdjacencySegments => _inner.AdjacencySegments;

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
        EnsureWritable();
        using var usage = EnterUsage();
        if (_vectors is null)
            throw new NotSupportedException("This backend does not support transaction-scoped SetVector.");
        // transaction-owned WalWriteSet 下で書く → グラフ変更と同じ WAL に乗り、
        // commit で原子確定し、プロセス内 abort は before-image で巻き戻る。
        _vectors.SetVector(kind, entityId, indexName, vector);
    }

    public void RemoveVector(Core.EntityKind kind, long entityId, string indexName)
    {
        EnsureWritable();
        using var usage = EnterUsage();
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
        EnsureWritable();
        using var usage = EnterUsage();
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Nexus type must not be null, empty, or whitespace.", nameof(type));
        var typeId = _nexusTypeTokens.GetOrCreate(type);
        return CreateNexusCore(typeId, members);
    }

    public NexusId CreateNexus(NexusTypeId typeId, ReadOnlySpan<NexusMember> members)
    {
        EnsureWritable();
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
        EnsureWritable();
        using var usage = EnterUsage();
        DeleteNexusCore(nexusId);
    }

    private void DeleteNexusCore(NexusId nexusId)
    {
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
        EntityRef owner = PropertyOwner(nexusId);
        var firstProperty = _inner.Nexuses.Read(nexusId).FirstPropertyRef;
        if (!firstProperty.IsValid) return;
        var toDelete = new List<PropertyVersionRef>();
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
            toDelete.Add(cursor.CurrentVersion);
        foreach (var version in toDelete)
            _inner.Properties.Delete(owner, version, firstProperty);
    }

    public NexusMemberEnumerator GetMembers(NexusId nexusId, string? role = null)
    {
        var usage = EnterUsage();
        RoleId roleFilter = RoleId.Invalid;
        if (role != null && _roleTokens.TryGet(role, out var rid))
            roleFilter = rid;
        else if (role != null)
        {
            usage.Dispose();
            return default;
        }

        try
        {
            var innerEnum = _inner.Incidences.EnumerateByNexus(nexusId, _inner.Nexuses);
            var enumerator = new NexusMemberEnumerator(
                innerEnum, _roleTokens, _inner.Vertices, roleFilter);
            enumerator.AttachUsage(usage);
            return enumerator;
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    public NexusIdEnumerator GetNexuses(VertexId vertexId, string? type = null, string? role = null)
    {
        var usage = EnterUsage();
        NexusTypeId typeFilter = NexusTypeId.Invalid;
        if (type != null && _nexusTypeTokens.TryGet(type, out var tid))
            typeFilter = tid;
        else if (type != null)
        {
            usage.Dispose();
            return default;
        }

        RoleId roleFilter = RoleId.Invalid;
        if (role != null && _roleTokens.TryGet(role, out var rid))
            roleFilter = rid;
        else if (role != null)
        {
            usage.Dispose();
            return default;
        }

        try
        {
            var innerEnum = _inner.Incidences.EnumerateByVertex(
                vertexId, _inner.VertexIncidenceHeads, _inner.Nexuses);
            var enumerator = new NexusIdEnumerator(
                innerEnum, _inner.Nexuses, typeFilter, roleFilter);
            enumerator.AttachUsage(usage);
            return enumerator;
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    public string? GetNexusTypeName(NexusTypeId typeId)
        => typeId.IsValid ? _nexusTypeTokens.GetName(typeId) : null;

    // ========== Nexusプロパティ操作 ==========
    // vertex / edge と同じ owner-bound property version store を使う。

    public void SetProperty(NexusId nexusId, string key, in PropertyValue value)
    {
        EnsureWritable();
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
        EntityRef owner = PropertyOwner(nexusId);
        var firstProperty = _inner.Nexuses.Read(nexusId).FirstPropertyRef;
        var newHead = SetSingleProperty(owner, firstProperty, keyId, in value);
        var wh = _inner.Nexuses.Write(nexusId);
        wh.FirstPropertyRef = newHead;
        wh.Dispose();
    }

    public PropertyValue GetProperty(NexusId nexusId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return default;
        if (_propKeyTokens.GetCardinality(keyId) == PropertyCardinality.Set)
            throw new InvalidOperationException($"Use GetPropertyValues for Set-cardinality property '{key}'.");
        var firstProperty = _inner.Nexuses.Read(nexusId).FirstPropertyRef;
        return GetPropertyCore(PropertyOwner(nexusId), firstProperty, keyId);
    }

    public bool HasProperty(NexusId nexusId, string key)
    {
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return false;
        var firstProperty = _inner.Nexuses.Read(nexusId).FirstPropertyRef;
        return HasPropertyCore(PropertyOwner(nexusId), firstProperty, keyId);
    }

    public void RemoveProperty(NexusId nexusId, string key)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;

        var firstProperty = _inner.Nexuses.Read(nexusId).FirstPropertyRef;
        bool removed = RemovePropertyCore(PropertyOwner(nexusId), firstProperty, keyId);
        if (removed)
        {
            _columns?.OnRemoveProperty(Core.EntityKind.Nexus, nexusId.Sequence, keyId, _inner.Id.Value);
            if (_logicalSink != null)
                RecordLogical(LogicalMutation.RemoveNexusProperty(nexusId, key));
        }
    }

    public PropertyCursor EnumerateProperties(NexusId nexusId)
    {
        var usage = EnterUsage();
        try
        {
            var cursor = _inner.Nexuses.EnumerateProperties(nexusId, _inner.Properties);
            cursor.AttachUsage(usage);
            return cursor;
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    // ── Nexusのマルチバリュープロパティ (Set cardinality) ──

    public void AddPropertyValue(NexusId nexusId, string key, in PropertyValue value)
    {
        EnsureWritable();
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

        var firstProperty = _inner.Nexuses.Read(nexusId).FirstPropertyRef;
        var newHead = _inner.Properties.Create(
            new PropertyAddress(PropertyOwner(nexusId), keyId),
            PropertyCardinality.Set,
            in value,
            firstProperty);
        var wh = _inner.Nexuses.Write(nexusId);
        wh.FirstPropertyRef = newHead;
        wh.Dispose();

        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.AddNexusPropertyValue(nexusId, key, in captured));
        }
    }

    public void RemovePropertyValue(NexusId nexusId, string key, in PropertyValue value)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (_propKeyTokens.GetCardinality(keyId) != PropertyCardinality.Set)
            throw new InvalidOperationException($"Use RemoveProperty for Single-cardinality property '{key}'.");

        var firstProperty = _inner.Nexuses.Read(nexusId).FirstPropertyRef;
        EntityRef owner = PropertyOwner(nexusId);
        var cursor = _inner.Properties.Enumerate(owner, firstProperty);
        while (cursor.MoveNext())
        {
            if (cursor.Current.KeyId == keyId
                && PropertyValueEqualityHelper.AreEqual(cursor.Current.Value, in value))
            {
                _inner.Properties.Delete(owner, cursor.CurrentVersion, firstProperty);

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
        var usage = EnterUsage();
        if (!_propKeyTokens.TryGet(key, out var keyId))
        {
            usage.Dispose();
            return new PropertyValuesEnumerator(
                new PropertyCursor(null!, default, PropertyVersionRef.Invalid), default);
        }
        try
        {
            var cursor = _inner.Nexuses.EnumerateProperties(nexusId, _inner.Properties);
            cursor.AttachUsage(usage);
            return new PropertyValuesEnumerator(cursor, keyId);
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    public void Commit()
    {
        _inner.Commit();
    }

    public void Rollback()
    {
        _inner.Abort();
    }

    public void Dispose()
    {
        _inner.Dispose();
    }

    // savepoint / nested undo — 下層トランザクションへ委譲する。
    public SavepointId Savepoint(string? name = null)
    {
        return _inner.Savepoint(name);
    }

    public void RollbackTo(SavepointId savepoint)
    {
        _inner.RollbackTo(savepoint);
    }

    public void ReleaseSavepoint(SavepointId savepoint)
    {
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

    private void EnsureWritable()
    {
        if (IsReadOnly)
            throw new TransactionException("Cannot mutate through a read-only transaction.");
    }
}

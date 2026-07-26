using System.Diagnostics;
using Quiver.Core;
using Quiver.Index;
using Quiver.Index.Vector;
using Quiver.Index.FullText;
using Quiver.Telemetry;
using Quiver.Logical;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Quiver.Api;

namespace Quiver;

internal sealed class GraphTransaction : IWriteTransaction, IReadTransactionInternal
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
    private readonly VectorSegmentIndex? _vectorSegments;
    private List<VectorSegmentMutation>? _vectorMutations;
    private readonly FullTextSegmentIndex? _fullTextSegments;
    private List<FullTextSegmentMutation>? _fullTextMutations;
    private bool _fullTextPrepared;
    private List<(long Id, int MutationCount)>? _fullTextSavepoints;
    private readonly ISchemaCatalog _schema;
    private readonly ISchemaEditor? _schemaEditor;
    private readonly NexusMergeIndex? _nexusMergeIndex;

    internal GraphTransaction(
        ITransaction inner,
        ITokenStore<LabelId> labelTokens,
        ITokenStore<EdgeTypeId> edgeTypeTokens,
        PropertyKeyTokenStore propKeyTokens,
        ITokenStore<NexusTypeId> nexusTypeTokens,
        ITokenStore<RoleId> roleTokens,
        ISchemaCatalog schema,
        bool isReadOnly = false,
        ILogicalMutationSink? logicalSink = null,
        VectorSegmentIndex? vectorSegments = null,
        FullTextSegmentIndex? fullTextSegments = null,
        NexusMergeIndex? nexusMergeIndex = null)
    {
        _inner = inner;
        _labelTokens = labelTokens;
        _edgeTypeTokens = edgeTypeTokens;
        _propKeyTokens = propKeyTokens;
        _nexusTypeTokens = nexusTypeTokens;
        _roleTokens = roleTokens;
        _schema = schema;
        _schemaEditor = schema as ISchemaEditor;
        IsReadOnly = isReadOnly;
        _logicalSink = logicalSink;
        _vectorSegments = vectorSegments;
        _fullTextSegments = fullTextSegments;
        _nexusMergeIndex = nexusMergeIndex;
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
    public ISchemaCatalog Schema => _schema;
    public ISchemaEditor EditSchema
        => _schemaEditor
            ?? throw new TransactionException(
                "Cannot edit schema in a read-only transaction.");
    public GraphTraversalSource Query => new(this, _schema);
    public GraphMutationSource Mutate => new(this);

    // MigrationContext.ForEachVertex が Access.ScanVertices に渡す。
    internal ITransaction Inner => _inner;
    ITransaction IReadTransactionInternal.Inner => _inner;
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

        RemoveFullTextIndexEntries(PropertyOwner(vertexId));

        _inner.Vertices.Free(vertexId);
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

    public string? GetEdgeType(EdgeId edgeId)
    {
        using var usage = EnterUsage();
        if (!IsLogicalEdgeIdentity(edgeId)) return null;
        EdgeReadHandle edge = _inner.Edges.Read(edgeId);
        return edge.InUse && edge.Type.IsValid
            ? _edgeTypeTokens.GetName(edge.Type)
            : null;
    }

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

        ScalarIndexDefinition? mergeIndex = _inner.Indexes.ListIndexDefinitions()
            .FirstOrDefault(x =>
                x.State == IndexLifecycleState.Ready
                && x.Definition.Target.OwnerKind == PropertyOwnerKind.Vertex
                && x.Definition.Target.PropertyKey == matchKey
                && x.Definition.Target.Scope == label)
            .Definition;
        if (mergeIndex is not null)
        {
            var probe = ScalarIndexProbe.Equal(in matchValue);
            foreach (long raw in SeekScalarValues(mergeIndex, in matchValue))
            {
                EntityRef? candidate = ResolveScalarCandidate(mergeIndex, raw, probe);
                if (candidate is { Kind: EntityKind.Vertex } owner)
                    return (new VertexId(owner.Value), false);
            }
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
                        " 'EditSchema.CreateIndex(new ScalarIndexDefinition(name, new PropertyTarget(PropertyOwnerKind.Vertex, propertyKey, label), kind))' で索引を作成すると O(log n) になります。",
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
        var version = SetVertexProperty(newId, newKeyId, in matchValue);
        MaintainScalarIndexes(PropertyOwner(newId), matchKey, in matchValue, version);
        return (newId, true);
    }

    private void MaintainScalarIndexes(
        EntityRef owner,
        string propertyKey,
        in PropertyValue value,
        PropertyVersionRef version)
    {
        foreach (ScalarIndexMetadata metadata in _inner.Indexes.ListIndexDefinitions())
        {
            ScalarIndexDefinition definition = metadata.Definition;
            if (metadata.State != IndexLifecycleState.Ready
                || definition.Target.PropertyKey != propertyKey
                || !MatchesTarget(owner, definition.Target))
                continue;

            InsertScalarValue(definition, in value, version.Value);
        }
    }

    private void InsertScalarValue(
        ScalarIndexDefinition definition,
        in PropertyValue value,
        long propertyVersion)
    {
        switch (definition.Kind)
        {
            case IndexKind.Int32Equality when value.Type == PropertyValueType.Int32:
                _inner.Indexes.CreateInt32Index(definition.Name)
                    .Insert(value.Int32Value, propertyVersion);
                break;
            case IndexKind.Int64Equality when value.Type is
                PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64:
                _inner.Indexes.CreateInt64Index(definition.Name)
                    .Insert(value.Int64Value, propertyVersion);
                break;
            case IndexKind.DoubleEquality when value.Type == PropertyValueType.Double:
                _inner.Indexes.CreateDoubleIndex(definition.Name)
                    .Insert(value.DoubleValue, propertyVersion);
                break;
            case IndexKind.StringEquality or IndexKind.StringRange
                when value.Type == PropertyValueType.String:
                _inner.Indexes.CreateStringIndex(definition.Name).Insert(
                    System.Text.Encoding.UTF8.GetString(value.Utf8StringValue),
                    propertyVersion);
                break;
        }
    }

    private ScalarIndexDefinition? FindReadyScalarIndex(string indexName)
        => _inner.Indexes.ListIndexDefinitions()
            .FirstOrDefault(x =>
                x.State == IndexLifecycleState.Ready
                && x.Definition.Name == indexName)
            .Definition;

    private bool TryFindScalarIndex(
        string indexName,
        out ScalarIndexMetadata metadata)
    {
        metadata = _inner.Indexes.ListIndexDefinitions()
            .FirstOrDefault(x => x.Definition.Name == indexName);
        return metadata.Definition is not null;
    }

    private IEnumerable<long> SeekScalarValues(
        ScalarIndexDefinition definition,
        in PropertyValue key)
        => definition.Kind switch
        {
            IndexKind.Int32Equality when key.Type == PropertyValueType.Int32 =>
                _inner.Indexes.CreateInt32Index(definition.Name).SeekValues(key.Int32Value),
            IndexKind.Int64Equality when key.Type is
                PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64 =>
                _inner.Indexes.CreateInt64Index(definition.Name).SeekValues(key.Int64Value),
            IndexKind.DoubleEquality when key.Type == PropertyValueType.Double =>
                _inner.Indexes.CreateDoubleIndex(definition.Name).SeekValues(key.DoubleValue),
            IndexKind.StringEquality or IndexKind.StringRange
                when key.Type == PropertyValueType.String =>
                _inner.Indexes.CreateStringIndex(definition.Name).SeekValues(
                    System.Text.Encoding.UTF8.GetString(key.Utf8StringValue)),
            _ => [],
        };

    private EntityRef? ResolveScalarCandidate(
        ScalarIndexDefinition? definition,
        long propertyVersion,
        ScalarIndexProbe probe)
    {
        if (definition is null)
            return null;

        PropertyVersionRecord property = _inner.Properties.Read(
            new PropertyVersionRef(propertyVersion));
        PropertyValue propertyValue = property.Value;
        if (!property.InUse
            || _propKeyTokens.GetName(property.Address.Key) != definition.Target.PropertyKey
            || !MatchesTarget(property.Address.Owner, definition.Target)
            || !probe.Matches(in propertyValue))
            return null;

        return property.Address.Owner;
    }

    private IEnumerable<long> ScanScalarValues(
        ScalarIndexDefinition definition,
        ScalarIndexProbe probe)
    {
        var values = new List<(ScalarIndexSortKey Key, long Version)>();

        void Collect(EntityRef owner, PropertyCursor properties)
        {
            if (!MatchesTarget(owner, definition.Target))
                return;

            while (properties.MoveNext())
            {
                PropertyEntry current = properties.Current;
                PropertyValue currentValue = current.Value;
                if (_propKeyTokens.GetName(current.KeyId) != definition.Target.PropertyKey
                    || !probe.Matches(in currentValue)
                    || !ScalarIndexSortKey.TryCreate(
                        definition.Kind,
                        in currentValue,
                        out ScalarIndexSortKey sortKey))
                    continue;
                values.Add((sortKey, properties.CurrentVersion.Value));
            }
        }

        switch (definition.Target.OwnerKind)
        {
            case PropertyOwnerKind.Vertex:
                foreach (VertexId id in _inner.Vertices.Scan())
                    Collect(
                        EntityRef.From(id),
                        _inner.Vertices.EnumerateProperties(id, _inner.Properties));
                break;
            case PropertyOwnerKind.Edge:
                foreach (EdgeId id in _inner.Edges.Scan())
                    Collect(
                        EntityRef.From(id),
                        _inner.Edges.EnumerateProperties(id, _inner.Properties));
                break;
            case PropertyOwnerKind.Nexus:
                foreach (NexusId id in _inner.Nexuses.Scan())
                    Collect(
                        EntityRef.From(id),
                        _inner.Nexuses.EnumerateProperties(id, _inner.Properties));
                break;
        }

        values.Sort(static (left, right) =>
        {
            int keyOrder = left.Key.CompareTo(right.Key);
            return keyOrder != 0
                ? keyOrder
                : left.Version.CompareTo(right.Version);
        });
        return values.Select(x => x.Version);
    }

    private bool MatchesTarget(EntityRef owner, PropertyTarget target)
    {
        if (owner.Kind != target.OwnerKind switch
            {
                PropertyOwnerKind.Vertex => EntityKind.Vertex,
                PropertyOwnerKind.Edge => EntityKind.Edge,
                PropertyOwnerKind.Nexus => EntityKind.Nexus,
                _ => (EntityKind)0,
            })
            return false;

        return owner.Kind switch
        {
            EntityKind.Vertex => MatchesVertexTarget(owner, target.Scope),
            EntityKind.Edge => MatchesEdgeTarget(owner, target.Scope),
            EntityKind.Nexus => MatchesNexusTarget(owner, target.Scope),
            _ => false,
        };
    }

    private bool MatchesVertexTarget(EntityRef owner, string? scope)
    {
        var vertex = _inner.Vertices.Read(new VertexId(owner.Value));
        return vertex.InUse
            && (scope is null || _labelTokens.GetName(vertex.Label) == scope);
    }

    private bool MatchesEdgeTarget(EntityRef owner, string? scope)
    {
        var edge = _inner.Edges.Read(new EdgeId(owner.Value));
        return edge.InUse
            && (scope is null || _edgeTypeTokens.GetName(edge.Type) == scope);
    }

    private bool MatchesNexusTarget(EntityRef owner, string? scope)
    {
        using var nexus = _inner.Nexuses.Read(new NexusId(owner.Value));
        return nexus.InUse
            && (scope is null || _nexusTypeTokens.GetName(nexus.Type) == scope);
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
        foreach (EdgeId candidate in _inner.Edges.Lookup(source, target, typeId))
        {
            EdgeReadHandle edge = _inner.Edges.Read(candidate);
            if (edge.InUse
                && edge.Source == source
                && edge.Target == target
                && edge.Type == typeId)
                return (edge.Id, false);
        }
        return (CreateEdgeCore(source, target, typeId, type), true);
    }

    public void DeleteEdge(EdgeId edgeId)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!IsLogicalEdgeIdentity(edgeId)) return;
        DeleteEdgeCore(edgeId);
    }

    private void DeleteEdgeCore(EdgeId edgeId)
    {
        if (!_inner.Edges.Read(edgeId).InUse)
            return;
        RemoveFullTextIndexEntries(PropertyOwner(edgeId));
        FreeEdgeProperties(edgeId);
        // この ID が不変ベースビューに含まれる場合、隣接ブロックには依然として
        // 現れる — 後続の expand カーソルがスキップできるよう tombstone を記録する。
        // delta 側 ID に対してはストアは no-op。
        _inner.AdjacencySegments?.Tombstone(edgeId);
        _inner.Edges.Delete(_inner.Vertices, edgeId);
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
        // SetVertexProperty がチェーンを変更する前にキャプチャする — value は
        // ref struct のため、ヒープコピーは LogicalPropertyValue に閉じ込める。
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.SetVertexProperty(vertexId, key, in captured));
        }
        var version = SetVertexProperty(vertexId, keyId, in value);
        MaintainScalarIndexes(PropertyOwner(vertexId), key, in value, version);
        StageFullTextPropertyMutation(PropertyOwner(vertexId), key, version, in value);
    }

    public void SetProperty(EdgeId edgeId, string key, in PropertyValue value)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!IsLogicalEdgeIdentity(edgeId)) return;
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
        var version = SetEdgeProperty(edgeId, keyId, in value);
        MaintainScalarIndexes(PropertyOwner(edgeId), key, in value, version);
        StageFullTextPropertyMutation(PropertyOwner(edgeId), key, version, in value);
    }

    private PropertyVersionRef SetEdgeProperty(EdgeId edgeId, PropertyKeyId keyId, in PropertyValue value)
    {
        EntityRef owner = PropertyOwner(edgeId);
        var firstProperty = _inner.Edges.Read(edgeId).FirstPropertyRef;
        var newHead = SetSingleProperty(owner, firstProperty, keyId, in value);
        var wh = _inner.Edges.Write(edgeId);
        wh.FirstPropertyRef = newHead;
        wh.Dispose();
        return newHead;
    }

    private PropertyVersionRef SetVertexProperty(VertexId vertexId, PropertyKeyId keyId, in PropertyValue value)
    {
        EntityRef owner = PropertyOwner(vertexId);
        // property chain を変更してから owner を lock する順序では、ラッパーが保証する owner lock の
        // 外側で version が追加される。write handle を先に取得し、同じ pin で head の読書きを完結させる。
        var wh = _inner.Vertices.Write(vertexId);
        try
        {
            var newHead = SetSingleProperty(owner, wh.FirstPropertyRef, keyId, in value);
            wh.FirstPropertyRef = newHead;
            return newHead;
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

    private EntityRef PropertyOwner(VertexId id)
    {
        int generation = id.Generation == 0 ? _inner.Vertices.CurrentGeneration(id.Sequence) : id.Generation;
        return EntityRef.From(generation > 0 ? VertexId.Create(id.Sequence, generation) : id);
    }

    private EntityRef PropertyOwner(EdgeId id)
    {
        // Generation 0 は primary record で検証済みの内部物理参照だけに使う。
        // public 入力をここで current generation へ補うと、再利用前の raw Sequence が
        // 新しい incarnation の owner に別名化するため、公開境界では先に拒否する。
        int generation = id.Generation == 0 ? _inner.Edges.CurrentGeneration(id.Sequence) : id.Generation;
        return EntityRef.From(generation > 0 ? EdgeId.Create(id.Sequence, generation) : id);
    }

    private static bool IsLogicalEdgeIdentity(EdgeId id)
        => id.IsValid && id.Generation > 0;

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
            RemoveVectorIndexEntries(PropertyOwner(vertexId), key);
            RemoveFullTextIndexEntries(PropertyOwner(vertexId), key);
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
        if (!IsLogicalEdgeIdentity(edgeId)) return default;
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
        MaintainScalarIndexes(PropertyOwner(vertexId), key, in value, newHead);
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.AddVertexPropertyValue(
                vertexId,
                key,
                in captured));
        }
    }

    public void AddPropertyValue(EdgeId edgeId, string key, in PropertyValue value)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!IsLogicalEdgeIdentity(edgeId)) return;
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
        MaintainScalarIndexes(PropertyOwner(edgeId), key, in value, newHead);
        if (_logicalSink != null)
        {
            var captured = LogicalPropertyValue.Capture(in value);
            RecordLogical(LogicalMutation.AddEdgePropertyValue(
                edgeId,
                key,
                in captured));
        }
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
                if (_logicalSink != null)
                {
                    var captured = LogicalPropertyValue.Capture(in value);
                    RecordLogical(LogicalMutation.RemoveVertexPropertyValue(
                        vertexId,
                        key,
                        in captured));
                }
                return;
            }
        }
    }

    public void RemovePropertyValue(EdgeId edgeId, string key, in PropertyValue value)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!IsLogicalEdgeIdentity(edgeId)) return;
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
                if (_logicalSink != null)
                {
                    var captured = LogicalPropertyValue.Capture(in value);
                    RecordLogical(LogicalMutation.RemoveEdgePropertyValue(
                        edgeId,
                        key,
                        in captured));
                }
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

    public void RemoveProperty(EdgeId edgeId, string key)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (!IsLogicalEdgeIdentity(edgeId)) return;
        if (!_propKeyTokens.TryGet(key, out var keyId)) return;
        if (!_inner.Edges.Read(edgeId).InUse) return;

        var firstProperty = _inner.Edges.Read(edgeId).FirstPropertyRef;
        EntityRef owner = PropertyOwner(edgeId);
        if (!RemovePropertyCore(owner, firstProperty, keyId)) return;

        RemoveVectorIndexEntries(owner, key);
        RemoveFullTextIndexEntries(owner, key);
        if (_logicalSink != null)
            RecordLogical(LogicalMutation.RemoveEdgeProperty(edgeId, key));
    }

    public PropertyValuesEnumerator GetPropertyValues(EdgeId edgeId, string key)
    {
        var usage = EnterUsage();
        if (!IsLogicalEdgeIdentity(edgeId))
        {
            usage.Dispose();
            return new PropertyValuesEnumerator(
                new PropertyCursor(null!, default, PropertyVersionRef.Invalid), default);
        }
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
        if (typeFilter is not null
            && !_edgeTypeTokens.TryGet(typeFilter, out _))
        {
            usage.Dispose();
            return new EdgeEnumerator(
                _inner.Edges,
                _inner.Vertices,
                vertexId,
                EdgeId.Invalid);
        }
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

    public EntityRefEnumerator SeekIndex(string indexName, in PropertyValue key)
    {
        var usage = EnterUsage();
        try
        {
            bool found = TryFindScalarIndex(indexName, out var metadata);
            ScalarIndexDefinition? definition = found ? metadata.Definition : null;
            var probe = ScalarIndexProbe.Equal(in key);
            IEnumerable<long> values = definition is null
                ? []
                : metadata.State == IndexLifecycleState.Ready
                    ? SeekScalarValues(definition, in key)
                    : ScanScalarValues(definition, probe);
            return new EntityRefEnumerator(
                values,
                raw => ResolveScalarCandidate(definition, raw, probe),
                usage);
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    public EntityRefEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive)
    {
        var usage = EnterUsage();
        try
        {
            bool found = TryFindScalarIndex(indexName, out var metadata);
            ScalarIndexDefinition? definition = found ? metadata.Definition : null;
            IEnumerable<long> values;
            if (definition is not null
                && metadata.State != IndexLifecycleState.Ready)
            {
                var fallbackProbe = ScalarIndexProbe.Range(
                    in from, fromInclusive, in to, toInclusive);
                values = ScanScalarValues(definition, fallbackProbe);
            }
            else switch (definition?.Kind)
            {
                case IndexKind.Int32Equality when from.Type == PropertyValueType.Int32:
                    values = _inner.Indexes.CreateInt32Index(indexName).RangeValues(
                        from.Int32Value, fromInclusive, to.Int32Value, toInclusive);
                    break;
                case IndexKind.Int64Equality when from.Type is
                    PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64:
                    values = _inner.Indexes.CreateInt64Index(indexName).RangeValues(
                        from.Int64Value, fromInclusive, to.Int64Value, toInclusive);
                    break;
                case IndexKind.DoubleEquality when from.Type == PropertyValueType.Double:
                    values = _inner.Indexes.CreateDoubleIndex(indexName).RangeValues(
                        from.DoubleValue, fromInclusive, to.DoubleValue, toInclusive);
                    break;
                case IndexKind.StringEquality or IndexKind.StringRange
                    when from.Type == PropertyValueType.String:
                    string fromStr = System.Text.Encoding.UTF8.GetString(from.Utf8StringValue);
                    string toStr   = System.Text.Encoding.UTF8.GetString(to.Utf8StringValue);
                    values = _inner.Indexes.CreateStringIndex(indexName).RangeValues(
                        fromStr, fromInclusive, toStr, toInclusive);
                    break;
                default:
                    values = [];
                    break;
            }
            var probe = ScalarIndexProbe.Range(
                in from, fromInclusive, in to, toInclusive);
            return new EntityRefEnumerator(
                values,
                raw => ResolveScalarCandidate(definition, raw, probe),
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

    // ========== vector property ==========

    public void SetVectorProperty(
        Core.EntityRef owner,
        string propertyKey,
        ReadOnlySpan<float> vector)
    {
        EnsureWritable();
        ArgumentException.ThrowIfNullOrEmpty(propertyKey);
        if (!owner.IsValid)
            throw new ArgumentException("有効な owner が必要です。", nameof(owner));

        PropertyValue value = PropertyValue.FromFloatArray(vector);
        switch (owner.Kind)
        {
            case Core.EntityKind.Vertex:
                SetProperty(new VertexId(owner.Value), propertyKey, in value);
                break;
            case Core.EntityKind.Edge:
                SetProperty(new EdgeId(owner.Value), propertyKey, in value);
                break;
            case Core.EntityKind.Nexus:
                SetProperty(new NexusId(owner.Value), propertyKey, in value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(owner));
        }

        // index の存在を primary property の保存条件にしない。
        // definition に一致する場合だけ同じ transaction-owned WAL batch へ delta を加える。
        foreach (IndexInfo index in _schema.ListIndexes())
        {
            if (index.Definition is not VectorIndexDefinition vectorIndex
                || vectorIndex.Target.PropertyKey != propertyKey
                || !MatchesOwner(owner, vectorIndex.Target))
                continue;
            StageVectorMutation(VectorSegmentMutation.Upsert(vectorIndex, owner, vector));
        }
    }

    public bool TryGetVectorProperty(
        Core.EntityRef owner,
        string propertyKey,
        Span<float> destination)
    {
        if (!owner.IsValid || string.IsNullOrEmpty(propertyKey))
            return false;

        PropertyValue value;
        switch (owner.Kind)
        {
            case Core.EntityKind.Vertex:
                value = GetProperty(new VertexId(owner.Value), propertyKey);
                break;
            case Core.EntityKind.Edge:
                value = GetProperty(new EdgeId(owner.Value), propertyKey);
                break;
            case Core.EntityKind.Nexus:
                value = GetProperty(new NexusId(owner.Value), propertyKey);
                break;
            default:
                return false;
        }
        if (value.Type != PropertyValueType.FloatArray
            || destination.Length < value.FloatArrayValue.Length)
            return false;
        value.FloatArrayValue.CopyTo(destination);
        return true;
    }

    public VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
    {
        var usage = EnterUsage();
        try
        {
            options = VectorSearchOptionsValidator.Normalize(options);
            if (k <= 0)
                throw new ArgumentOutOfRangeException(nameof(k));
            if (!_schema.TryGetIndex(indexName, out IndexInfo index)
                || index.Definition is not VectorIndexDefinition definition)
            {
                throw new VectorException($"Vector index '{indexName}' does not exist.");
            }
            if (query.Length != definition.Dimensions)
            {
                throw new VectorException(
                    $"Vector index '{indexName}' expects {definition.Dimensions} dimensions, got {query.Length}.");
            }

            // derived segment は候補生成に使えてもsnapshot visibilityの正本にはできない。
            // primary property scanで候補を再検証する経路を常に保持し、drop/rebuild中も値を失わない。
            VectorSearchResult[] hits = [];
            if (_vectorSegments is not null)
            {
                VectorSegmentSearchResult segmentResult = _vectorSegments.Search(
                    _inner.Snapshot,
                    definition,
                    query,
                    k,
                    options);
                var segmentHeap = new VectorKnnHeap(k);
                var seen = new HashSet<EntityRef>();
                foreach (VectorSegmentCandidate candidate in segmentResult.Candidates)
                {
                    if (!seen.Add(candidate.Owner)
                        || !TryValidateSegmentCandidate(
                            definition,
                            candidate,
                            query,
                            out VectorSearchResult validated))
                        continue;
                    segmentHeap.Offer(validated);
                }
                hits = segmentHeap.ToSortedArray();
                if (!segmentResult.CoversPrimarySnapshot || hits.Length < k || !IsReadOnly)
                    hits = [];
            }
            if (hits.Length == 0)
            {
                var heap = new VectorKnnHeap(k);
                if (_propKeyTokens.TryGet(definition.Target.PropertyKey, out PropertyKeyId keyId))
                    ScanPrimaryVectors(definition, keyId, query, heap);
                hits = heap.ToSortedArray();
            }
            VectorSearchCursor cursor = new MaterializedVectorSearchCursor(hits);
            return new TransactionVectorSearchCursor(cursor, usage);
        }
        catch
        {
            usage.Dispose();
            throw;
        }
    }

    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k,
        VectorSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0)
            return [];
        options = VectorSearchOptionsValidator.Normalize(options);
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k));
        if (!_schema.TryGetIndex(indexName, out IndexInfo index)
            || index.Definition is not VectorIndexDefinition definition)
        {
            throw new VectorException($"Vector index '{indexName}' does not exist.");
        }
        for (int i = 0; i < queries.Count; i++)
        {
            if (queries[i].Length != definition.Dimensions)
            {
                throw new VectorException(
                    $"Vector index '{indexName}' expects {definition.Dimensions} dimensions, " +
                    $"got {queries[i].Length} at query {i}.");
            }
        }

        var heaps = new VectorKnnHeap[queries.Count];
        for (int i = 0; i < heaps.Length; i++)
            heaps[i] = new VectorKnnHeap(k);
        using (EnterUsage())
        {
            if (_propKeyTokens.TryGet(definition.Target.PropertyKey, out PropertyKeyId keyId))
                ScanPrimaryVectorsBatch(definition, keyId, queries, heaps);
        }

        var cursors = new VectorSearchCursor[queries.Count];
        for (int i = 0; i < queries.Count; i++)
        {
            VectorSearchCursor inner = new MaterializedVectorSearchCursor(
                heaps[i].ToSortedArray());
            cursors[i] = new TransactionVectorSearchCursor(inner, EnterUsage());
        }
        return cursors;
    }

    private bool MatchesOwner(Core.EntityRef owner, PropertyTarget target)
    {
        if (owner.Kind == Core.EntityKind.Nexus
            && target.OwnerKind == PropertyOwnerKind.Nexus)
        {
            if (target.Scope is null)
                return true;
            using NexusReadHandle nexus =
                _inner.Nexuses.Read(new NexusId(owner.Value));
            return nexus.InUse
                && _nexusTypeTokens.GetName(nexus.Type) == target.Scope;
        }
        return (owner.Kind, target.OwnerKind) switch
        {
            (Core.EntityKind.Vertex, PropertyOwnerKind.Vertex) =>
                target.Scope is null
                || _labelTokens.GetName(
                    _inner.Vertices.Read(new VertexId(owner.Value)).Label) == target.Scope,
            (Core.EntityKind.Edge, PropertyOwnerKind.Edge) =>
                target.Scope is null
                || _edgeTypeTokens.GetName(
                    _inner.Edges.Read(new EdgeId(owner.Value)).Type) == target.Scope,
            _ => false,
        };
    }

    private void ScanPrimaryVectors(
        VectorIndexDefinition definition,
        PropertyKeyId keyId,
        ReadOnlySpan<float> query,
        VectorKnnHeap heap)
    {
        switch (definition.Target.OwnerKind)
        {
            case PropertyOwnerKind.Vertex:
                foreach (VertexId id in _inner.Vertices.Scan())
                {
                    var vertex = _inner.Vertices.Read(id);
                    if (!vertex.InUse
                        || definition.Target.Scope is { } label
                            && _labelTokens.GetName(vertex.Label) != label)
                        continue;
                    OfferVector(
                        EntityRef.From(id),
                        _inner.Vertices.EnumerateProperties(id, _inner.Properties),
                        keyId,
                        definition.Metric,
                        query,
                        heap);
                }
                break;
            case PropertyOwnerKind.Edge:
                foreach (EdgeId id in _inner.Edges.Scan())
                {
                    var edge = _inner.Edges.Read(id);
                    if (!edge.InUse
                        || definition.Target.Scope is { } type
                            && _edgeTypeTokens.GetName(edge.Type) != type)
                        continue;
                    OfferVector(
                        EntityRef.From(id),
                        _inner.Edges.EnumerateProperties(id, _inner.Properties),
                        keyId,
                        definition.Metric,
                        query,
                        heap);
                }
                break;
            case PropertyOwnerKind.Nexus:
                foreach (NexusId id in _inner.Nexuses.Scan())
                {
                    using var nexus = _inner.Nexuses.Read(id);
                    if (!nexus.InUse
                        || definition.Target.Scope is { } type
                            && _nexusTypeTokens.GetName(nexus.Type) != type)
                        continue;
                    OfferVector(
                        EntityRef.From(id),
                        _inner.Nexuses.EnumerateProperties(id, _inner.Properties),
                        keyId,
                        definition.Metric,
                        query,
                        heap);
                }
                break;
        }
    }

    private void ScanPrimaryVectorsBatch(
        VectorIndexDefinition definition,
        PropertyKeyId keyId,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        VectorKnnHeap[] heaps)
    {
        switch (definition.Target.OwnerKind)
        {
            case PropertyOwnerKind.Vertex:
                foreach (VertexId id in _inner.Vertices.Scan())
                {
                    var vertex = _inner.Vertices.Read(id);
                    if (!vertex.InUse
                        || definition.Target.Scope is { } label
                            && _labelTokens.GetName(vertex.Label) != label)
                        continue;
                    OfferVectorBatch(
                        EntityRef.From(id),
                        _inner.Vertices.EnumerateProperties(id, _inner.Properties),
                        keyId,
                        definition.Metric,
                        queries,
                        heaps);
                }
                break;
            case PropertyOwnerKind.Edge:
                foreach (EdgeId id in _inner.Edges.Scan())
                {
                    var edge = _inner.Edges.Read(id);
                    if (!edge.InUse
                        || definition.Target.Scope is { } type
                            && _edgeTypeTokens.GetName(edge.Type) != type)
                        continue;
                    OfferVectorBatch(
                        EntityRef.From(id),
                        _inner.Edges.EnumerateProperties(id, _inner.Properties),
                        keyId,
                        definition.Metric,
                        queries,
                        heaps);
                }
                break;
            case PropertyOwnerKind.Nexus:
                foreach (NexusId id in _inner.Nexuses.Scan())
                {
                    using var nexus = _inner.Nexuses.Read(id);
                    if (!nexus.InUse
                        || definition.Target.Scope is { } type
                            && _nexusTypeTokens.GetName(nexus.Type) != type)
                        continue;
                    OfferVectorBatch(
                        EntityRef.From(id),
                        _inner.Nexuses.EnumerateProperties(id, _inner.Properties),
                        keyId,
                        definition.Metric,
                        queries,
                        heaps);
                }
                break;
        }
    }

    private static void OfferVectorBatch(
        EntityRef owner,
        PropertyCursor properties,
        PropertyKeyId keyId,
        DistanceMetric metric,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        VectorKnnHeap[] heaps)
    {
        while (properties.MoveNext())
        {
            PropertyEntry property = properties.Current;
            if (property.KeyId != keyId
                || property.Value.Type != PropertyValueType.FloatArray)
                continue;
            ReadOnlySpan<float> vector = property.Value.FloatArrayValue;
            for (int query = 0; query < queries.Count; query++)
            {
                heaps[query].Offer(new VectorSearchResult(
                    owner,
                    VectorMetrics.Score(metric, queries[query].Span, vector)));
            }
            return;
        }
    }

    private static void OfferVector(
        EntityRef owner,
        PropertyCursor properties,
        PropertyKeyId keyId,
        DistanceMetric metric,
        ReadOnlySpan<float> query,
        VectorKnnHeap heap)
    {
        while (properties.MoveNext())
        {
            PropertyEntry property = properties.Current;
            if (property.KeyId != keyId
                || property.Value.Type != PropertyValueType.FloatArray
                || property.Value.FloatArrayValue.Length != query.Length)
                continue;
            heap.Offer(new VectorSearchResult(
                owner,
                VectorMetrics.Score(metric, query, property.Value.FloatArrayValue)));
            return;
        }
    }

    private bool TryValidateSegmentCandidate(
        VectorIndexDefinition definition,
        VectorSegmentCandidate candidate,
        ReadOnlySpan<float> query,
        out VectorSearchResult result)
    {
        result = default;
        if (!MatchesOwner(candidate.Owner, definition.Target)
            || !_propKeyTokens.TryGet(
                definition.Target.PropertyKey,
                out PropertyKeyId keyId))
            return false;

        PropertyCursor properties = candidate.Owner.Kind switch
        {
            EntityKind.Vertex => _inner.Vertices.EnumerateProperties(
                new VertexId(candidate.Owner.Value),
                _inner.Properties),
            EntityKind.Edge => _inner.Edges.EnumerateProperties(
                new EdgeId(candidate.Owner.Value),
                _inner.Properties),
            EntityKind.Nexus => _inner.Nexuses.EnumerateProperties(
                new NexusId(candidate.Owner.Value),
                _inner.Properties),
            _ => default,
        };
        while (properties.MoveNext())
        {
            PropertyEntry property = properties.Current;
            if (property.KeyId != keyId
                || property.Value.Type != PropertyValueType.FloatArray
                || property.Value.FloatArrayValue.Length != definition.Dimensions
                || VectorPayloadChecksum.Compute(property.Value.FloatArrayValue)
                    != candidate.PayloadChecksum)
                continue;
            result = new(
                candidate.Owner,
                VectorMetrics.Score(
                    definition.Metric,
                    query,
                    property.Value.FloatArrayValue));
            return true;
        }
        return false;
    }

    private void RemoveVectorIndexEntries(Core.EntityRef owner, string propertyKey)
    {
        PropertyOwnerKind ownerKind = owner.Kind switch
        {
            Core.EntityKind.Vertex => PropertyOwnerKind.Vertex,
            Core.EntityKind.Edge => PropertyOwnerKind.Edge,
            Core.EntityKind.Nexus => PropertyOwnerKind.Nexus,
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };
        foreach (IndexInfo index in _schema.ListIndexes())
        {
            if (index.Definition is VectorIndexDefinition vectorIndex
                && vectorIndex.Target.OwnerKind == ownerKind
                && vectorIndex.Target.PropertyKey == propertyKey)
            {
                StageVectorMutation(VectorSegmentMutation.Tombstone(vectorIndex, owner));
            }
        }
    }

    private void StageVectorMutation(VectorSegmentMutation mutation)
    {
        if (_vectorSegments is null)
            return;
        if (_vectorMutations is null)
        {
            _vectorMutations = [];
            _inner.OnCommitted(PublishVectorMutations);
        }
        _vectorMutations.Add(mutation);
    }

    private void PublishVectorMutations()
    {
        if (_vectorSegments is null || _vectorMutations is not { Count: > 0 })
            return;
        _vectorSegments.PublishDelta(_inner.Id.Value, _vectorMutations);
    }

    private void StageFullTextPropertyMutation(
        EntityRef owner,
        string propertyKey,
        PropertyVersionRef propertyVersion,
        in PropertyValue value)
    {
        string? text = value.Type == PropertyValueType.String
            ? System.Text.Encoding.UTF8.GetString(value.Utf8StringValue)
            : null;
        foreach (IndexInfo index in _schema.ListIndexes())
        {
            if (index.Definition is FullTextIndexDefinition fullText
                && fullText.Target.PropertyKey == propertyKey
                && MatchesTarget(owner, fullText.Target))
            {
                StageFullTextMutation(new(
                    fullText,
                    owner,
                    propertyVersion,
                    text));
            }
        }
    }

    private void RemoveFullTextIndexEntries(
        EntityRef owner,
        string? propertyKey = null)
    {
        foreach (IndexInfo index in _schema.ListIndexes())
        {
            if (index.Definition is FullTextIndexDefinition fullText
                && (propertyKey is null || fullText.Target.PropertyKey == propertyKey)
                && MatchesTarget(owner, fullText.Target))
            {
                StageFullTextMutation(new(
                    fullText,
                    owner,
                    PropertyVersionRef.Invalid,
                    null));
            }
        }
    }

    private void StageFullTextMutation(FullTextSegmentMutation mutation)
    {
        if (_fullTextSegments is null)
            return;
        if (_fullTextMutations is null)
        {
            _fullTextMutations = [];
            _fullTextSegments.RegisterPending(
                _inner.Id.Value,
                _fullTextMutations);
            _inner.OnRolledBack(
                () => _fullTextSegments.DiscardPending(_inner.Id.Value));
        }
        _fullTextMutations.Add(mutation);
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

    public (NexusId Id, bool Created) MergeNexus(
        string type,
        ReadOnlySpan<NexusMember> members)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException(
                "Nexus type must not be null, empty, or whitespace.",
                nameof(type));

        NexusTypeId typeId = _nexusTypeTokens.GetOrCreate(type);
        IncidenceMember[] resolved = ResolveNexusMembers(members);
        if (_nexusMergeIndex is not null
            && _nexusMergeIndex.TryFind(
                _inner,
                typeId,
                resolved,
                out NexusId existing))
            return (existing, false);
        return (CreateNexusCore(typeId, members, resolved), true);
    }

    private NexusId CreateNexusCore(NexusTypeId typeId, ReadOnlySpan<NexusMember> members)
    {
        IncidenceMember[] resolved = ResolveNexusMembers(members);
        return CreateNexusCore(typeId, members, resolved);
    }

    private NexusId CreateNexusCore(
        NexusTypeId typeId,
        ReadOnlySpan<NexusMember> members,
        ReadOnlySpan<IncidenceMember> resolved)
    {
        var nexusId = _inner.Nexuses.Create(
            typeId,
            resolved,
            _inner.Incidences,
            _inner.VertexIncidenceHeads);
        _nexusMergeIndex?.Add(nexusId, typeId, resolved);

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

    private IncidenceMember[] ResolveNexusMembers(
        ReadOnlySpan<NexusMember> members)
    {
        if (members.Length < 2)
            throw new ArgumentException("A nexus requires at least 2 members.", nameof(members));

        var resolved = new IncidenceMember[members.Length];

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
        return resolved;
    }

    public void DeleteNexus(NexusId nexusId)
    {
        EnsureWritable();
        using var usage = EnterUsage();
        DeleteNexusCore(nexusId);
    }

    private void DeleteNexusCore(NexusId nexusId)
    {
        RemoveFullTextIndexEntries(PropertyOwner(nexusId));
        // header の可視性が incidence とプロパティの可視性の正本 — header を論理削除すれば
        // それらも同スナップショットで不可視になる。overflow プロパティレコードは物理的に残る
        // ため、slot を回収できるよう先にチェーンを解放してから header をスタンプする。
        FreeNexusProperties(nexusId);
        _inner.Nexuses.Delete(nexusId);
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

    public string? GetNexusType(NexusId nexusId)
    {
        using var usage = EnterUsage();
        using NexusReadHandle nexus = _inner.Nexuses.Read(nexusId);
        return nexus.InUse && nexus.Type.IsValid
            ? _nexusTypeTokens.GetName(nexus.Type)
            : null;
    }

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
        var version = SetNexusProperty(nexusId, keyId, in value);
        MaintainScalarIndexes(PropertyOwner(nexusId), key, in value, version);
        StageFullTextPropertyMutation(PropertyOwner(nexusId), key, version, in value);
    }

    private PropertyVersionRef SetNexusProperty(NexusId nexusId, PropertyKeyId keyId, in PropertyValue value)
    {
        EntityRef owner = PropertyOwner(nexusId);
        var firstProperty = _inner.Nexuses.Read(nexusId).FirstPropertyRef;
        var newHead = SetSingleProperty(owner, firstProperty, keyId, in value);
        var wh = _inner.Nexuses.Write(nexusId);
        wh.FirstPropertyRef = newHead;
        wh.Dispose();
        return newHead;
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
            RemoveVectorIndexEntries(PropertyOwner(nexusId), key);
            RemoveFullTextIndexEntries(PropertyOwner(nexusId), key);
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
        MaintainScalarIndexes(PropertyOwner(nexusId), key, in value, newHead);

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
        try
        {
            if (!_fullTextPrepared
                && _fullTextSegments is not null
                && _fullTextMutations is { Count: > 0 })
            {
                _fullTextSegments.PrepareCommit(_inner, _fullTextMutations);
                _fullTextPrepared = true;
            }
            _inner.Commit();
        }
        catch
        {
            if (_inner.State == TransactionState.Active)
                _inner.Abort();
            throw;
        }
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
        SavepointId savepoint = _inner.Savepoint(name);
        (_fullTextSavepoints ??= []).Add((
            savepoint.Value,
            _fullTextMutations?.Count ?? 0));
        return savepoint;
    }

    public void RollbackTo(SavepointId savepoint)
    {
        _inner.RollbackTo(savepoint);
        if (_fullTextSavepoints is null)
            return;
        int index = _fullTextSavepoints.FindIndex(
            entry => entry.Id == savepoint.Value);
        if (index < 0)
            return;
        int mutationCount = _fullTextSavepoints[index].MutationCount;
        if (_fullTextMutations is { } mutations
            && mutations.Count > mutationCount)
            mutations.RemoveRange(
                mutationCount,
                mutations.Count - mutationCount);
        if (index + 1 < _fullTextSavepoints.Count)
            _fullTextSavepoints.RemoveRange(
                index + 1,
                _fullTextSavepoints.Count - index - 1);
    }

    public void ReleaseSavepoint(SavepointId savepoint)
    {
        _inner.ReleaseSavepoint(savepoint);
        if (_fullTextSavepoints is null)
            return;
        int index = _fullTextSavepoints.FindIndex(
            entry => entry.Id == savepoint.Value);
        if (index >= 0)
            _fullTextSavepoints.RemoveRange(
                index,
                _fullTextSavepoints.Count - index);
    }

    // post-commit / post-rollback フックの登録は下層トランザクションへ委譲する。
    // ユーザは IWriteTransaction 経由でフックを登録できる。
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

    private readonly record struct ScalarIndexProbe(
        PropertyValueType Type,
        bool IsRange,
        long FromScalar,
        long ToScalar,
        double FromDouble,
        double ToDouble,
        string? FromString,
        string? UpperString,
        bool FromInclusive,
        bool ToInclusive)
    {
        internal static ScalarIndexProbe Equal(in PropertyValue value)
            => value.Type switch
            {
                PropertyValueType.Bool => new(
                    value.Type, false, value.BoolValue ? 1 : 0, 0,
                    0, 0, null, null, true, true),
                PropertyValueType.Int32 => new(
                    value.Type, false, value.Int32Value, 0,
                    0, 0, null, null, true, true),
                PropertyValueType.Int64 => new(
                    value.Type, false, value.Int64Value, 0,
                    0, 0, null, null, true, true),
                PropertyValueType.Double => new(
                    value.Type, false, 0, 0,
                    value.DoubleValue, 0, null, null, true, true),
                PropertyValueType.String => new(
                    value.Type, false, 0, 0, 0, 0,
                    System.Text.Encoding.UTF8.GetString(value.Utf8StringValue),
                    null, true, true),
                _ => default,
            };

        internal static ScalarIndexProbe Range(
            in PropertyValue from,
            bool fromInclusive,
            in PropertyValue to,
            bool toInclusive)
        {
            if (from.Type != to.Type)
                return default;
            ScalarIndexProbe start = Equal(in from);
            ScalarIndexProbe end = Equal(in to);
            return start with
            {
                IsRange = true,
                ToScalar = end.FromScalar,
                ToDouble = end.FromDouble,
                UpperString = end.FromString,
                FromInclusive = fromInclusive,
                ToInclusive = toInclusive,
            };
        }

        internal bool Matches(in PropertyValue value)
        {
            if (value.Type != Type)
                return false;

            int lower;
            int upper;
            switch (Type)
            {
                case PropertyValueType.Bool:
                {
                    long current = value.BoolValue ? 1 : 0;
                    lower = current.CompareTo(FromScalar);
                    upper = current.CompareTo(ToScalar);
                    break;
                }
                case PropertyValueType.Int32:
                    lower = value.Int32Value.CompareTo((int)FromScalar);
                    upper = value.Int32Value.CompareTo((int)ToScalar);
                    break;
                case PropertyValueType.Int64:
                    lower = value.Int64Value.CompareTo(FromScalar);
                    upper = value.Int64Value.CompareTo(ToScalar);
                    break;
                case PropertyValueType.Double:
                    lower = value.DoubleValue.CompareTo(FromDouble);
                    upper = value.DoubleValue.CompareTo(ToDouble);
                    break;
                case PropertyValueType.String:
                {
                    string current = System.Text.Encoding.UTF8.GetString(value.Utf8StringValue);
                    lower = string.CompareOrdinal(current, FromString);
                    upper = string.CompareOrdinal(current, UpperString);
                    break;
                }
                default:
                    return false;
            }

            if (!IsRange)
                return lower == 0;
            return (FromInclusive ? lower >= 0 : lower > 0)
                && (ToInclusive ? upper <= 0 : upper < 0);
        }
    }

    private readonly record struct ScalarIndexSortKey(
        IndexKind Kind,
        long Scalar,
        double FloatingPoint,
        string? Text) : IComparable<ScalarIndexSortKey>
    {
        internal static bool TryCreate(
            IndexKind kind,
            in PropertyValue value,
            out ScalarIndexSortKey key)
        {
            key = kind switch
            {
                IndexKind.Int32Equality when value.Type == PropertyValueType.Int32
                    => new(kind, value.Int32Value, 0, null),
                IndexKind.Int64Equality when value.Type is
                    PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64
                    => new(kind, value.Int64Value, 0, null),
                IndexKind.DoubleEquality when value.Type == PropertyValueType.Double
                    => new(kind, 0, value.DoubleValue, null),
                IndexKind.StringEquality or IndexKind.StringRange
                    when value.Type == PropertyValueType.String
                    => new(
                        kind,
                        0,
                        0,
                        System.Text.Encoding.UTF8.GetString(value.Utf8StringValue)),
                _ => default,
            };
            return kind switch
            {
                IndexKind.Int32Equality => value.Type == PropertyValueType.Int32,
                IndexKind.Int64Equality => value.Type is
                    PropertyValueType.Bool or PropertyValueType.Int32 or PropertyValueType.Int64,
                IndexKind.DoubleEquality => value.Type == PropertyValueType.Double,
                IndexKind.StringEquality or IndexKind.StringRange
                    => value.Type == PropertyValueType.String,
                _ => false,
            };
        }

        public int CompareTo(ScalarIndexSortKey other)
            => Kind switch
            {
                IndexKind.Int32Equality or IndexKind.Int64Equality
                    => Scalar.CompareTo(other.Scalar),
                IndexKind.DoubleEquality
                    => FloatingPoint.CompareTo(other.FloatingPoint),
                IndexKind.StringEquality or IndexKind.StringRange
                    => string.Compare(Text, other.Text, StringComparison.Ordinal),
                _ => 0,
            };
    }
}

internal sealed class TransactionVectorSearchCursor : VectorSearchCursor
{
    private readonly VectorSearchCursor _inner;
    private readonly TransactionUsageGuard _guard;

    internal TransactionVectorSearchCursor(
        VectorSearchCursor inner,
        TransactionUsageLease usage)
    {
        _inner = inner;
        _guard = usage.Guard;
        usage.Dispose();
    }

    public override bool MoveNext()
    {
        using var usage = _guard.Enter();
        return _inner.MoveNext();
    }

    public override VectorSearchResult Current
    {
        get
        {
            using var usage = _guard.Enter();
            return _inner.Current;
        }
    }

    public override void Dispose()
    {
        using var usage = _guard.Enter();
        _inner.Dispose();
    }
}

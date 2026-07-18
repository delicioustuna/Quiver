using Quiver.Api;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// 開始時点のスナップショットだけを公開する読み取りトランザクション。
/// この runtime type は書き込み capability を実装しない。
/// </summary>
public sealed class ReadTransaction : IReadTransaction, IReadTransactionInternal
{
    private readonly GraphTransaction _core;

    internal ReadTransaction(GraphTransaction core) => _core = core;

    /// <inheritdoc />
    public TransactionId Id => _core.Id;

    /// <inheritdoc />
    public TransactionState State => _core.State;

    /// <inheritdoc />
    public ISchemaCatalog Schema => _core.Schema;

    /// <inheritdoc />
    public GraphTraversalSource Query => new(this, Schema);

    /// <inheritdoc />
    public bool VertexExists(VertexId vertexId) => _core.VertexExists(vertexId);

    /// <inheritdoc />
    public string? GetVertexLabel(VertexId vertexId) => _core.GetVertexLabel(vertexId);

    /// <inheritdoc />
    public string? GetEdgeTypeName(EdgeTypeId typeId) => _core.GetEdgeTypeName(typeId);

    /// <inheritdoc />
    public string? GetEdgeType(EdgeId edgeId) => _core.GetEdgeType(edgeId);

    /// <inheritdoc />
    public PropertyValue GetProperty(VertexId vertexId, string key) => _core.GetProperty(vertexId, key);

    /// <inheritdoc />
    public PropertyValue GetProperty(EdgeId edgeId, string key) => _core.GetProperty(edgeId, key);

    /// <inheritdoc />
    public PropertyValue GetProperty(NexusId nexusId, string key) => _core.GetProperty(nexusId, key);

    /// <inheritdoc />
    public bool HasProperty(VertexId vertexId, string key) => _core.HasProperty(vertexId, key);

    /// <inheritdoc />
    public bool HasProperty(NexusId nexusId, string key) => _core.HasProperty(nexusId, key);

    /// <inheritdoc />
    public PropertyValuesEnumerator GetPropertyValues(VertexId vertexId, string key)
        => _core.GetPropertyValues(vertexId, key);

    /// <inheritdoc />
    public PropertyValuesEnumerator GetPropertyValues(EdgeId edgeId, string key)
        => _core.GetPropertyValues(edgeId, key);

    /// <inheritdoc />
    public PropertyValuesEnumerator GetPropertyValues(NexusId nexusId, string key)
        => _core.GetPropertyValues(nexusId, key);

    /// <inheritdoc />
    public PropertyCursor EnumerateProperties(VertexId vertexId) => _core.EnumerateProperties(vertexId);

    /// <inheritdoc />
    public PropertyCursor EnumerateProperties(NexusId nexusId) => _core.EnumerateProperties(nexusId);

    /// <inheritdoc />
    public EdgeEnumerator EnumerateEdges(
        VertexId vertexId,
        Direction direction = Direction.Both,
        string? typeFilter = null)
        => _core.EnumerateEdges(vertexId, direction, typeFilter);

    /// <inheritdoc />
    public EntityRefEnumerator SeekIndex(string indexName, in PropertyValue key)
        => _core.SeekIndex(indexName, in key);

    /// <inheritdoc />
    public EntityRefEnumerator RangeIndex(
        string indexName,
        in PropertyValue from,
        bool fromInclusive,
        in PropertyValue to,
        bool toInclusive)
        => _core.RangeIndex(indexName, in from, fromInclusive, in to, toInclusive);

    /// <inheritdoc />
    public bool TryGetVector(
        EntityKind kind,
        long entityId,
        string indexName,
        Span<float> destination)
        => _core.TryGetVector(kind, entityId, indexName, destination);

    /// <inheritdoc />
    public NexusMemberEnumerator GetMembers(NexusId nexusId, string? role = null)
        => _core.GetMembers(nexusId, role);

    /// <inheritdoc />
    public NexusIdEnumerator GetNexuses(
        VertexId vertexId,
        string? type = null,
        string? role = null)
        => _core.GetNexuses(vertexId, type, role);

    /// <inheritdoc />
    public string? GetNexusTypeName(NexusTypeId typeId) => _core.GetNexusTypeName(typeId);

    /// <inheritdoc />
    public string? GetNexusType(NexusId nexusId) => _core.GetNexusType(nexusId);

    /// <inheritdoc />
    public void Dispose() => _core.Dispose();

    ITransaction IReadTransactionInternal.Inner => _core.Inner;
    TransactionId IReadTransactionInternal.TransactionId => _core.TransactionId;
    TransactionUsageLease IReadTransactionInternal.EnterUsage() => _core.EnterUsage();
    IGraphAccessMethods IReadTransactionInternal.Access => _core.Access;
    IAdjacencySegmentStore? IReadTransactionInternal.AdjacencySegments => _core.AdjacencySegments;
    QueryResult IReadTransactionInternal.Execute(IPhysicalOperator plan) => _core.Execute(plan);
    IQueryCursor IReadTransactionInternal.ExecuteCursor(IPhysicalOperator plan) => _core.ExecuteCursor(plan);
    bool IReadTransactionInternal.TryColumnAggregate(
        EntityKind kind,
        string key,
        out ColumnAggregate result)
        => _core.TryColumnAggregate(kind, key, out result);
}

/// <summary>
/// snapshot read に graph/schema mutation と commit/abort capability を加えた
/// single-writer transaction。
/// </summary>
public sealed class WriteTransaction : IWriteTransaction, IReadTransactionInternal
{
    private readonly GraphTransaction _core;

    internal WriteTransaction(GraphTransaction core) => _core = core;

    /// <inheritdoc />
    public TransactionId Id => _core.Id;

    /// <inheritdoc />
    public TransactionState State => _core.State;

    /// <inheritdoc />
    public ISchemaCatalog Schema => _core.Schema;

    /// <inheritdoc />
    public ISchemaEditor EditSchema => _core.EditSchema;

    /// <inheritdoc />
    public GraphTraversalSource Query => new(this, Schema);

    /// <inheritdoc />
    public GraphMutationSource Mutate => new(this);

    /// <inheritdoc />
    public VertexId CreateVertex(string label) => _core.CreateVertex(label);
    /// <inheritdoc />
    public VertexId CreateVertex(LabelId labelId) => _core.CreateVertex(labelId);
    /// <inheritdoc />
    public void DeleteVertex(VertexId vertexId) => _core.DeleteVertex(vertexId);
    /// <inheritdoc />
    public bool VertexExists(VertexId vertexId) => _core.VertexExists(vertexId);
    /// <inheritdoc />
    public string? GetVertexLabel(VertexId vertexId) => _core.GetVertexLabel(vertexId);
    /// <inheritdoc />
    public (VertexId Id, bool Created) MergeVertex(
        string label,
        string matchKey,
        in PropertyValue matchValue)
        => _core.MergeVertex(label, matchKey, in matchValue);
    /// <inheritdoc />
    public string? GetEdgeTypeName(EdgeTypeId typeId) => _core.GetEdgeTypeName(typeId);

    /// <inheritdoc />
    public string? GetEdgeType(EdgeId edgeId) => _core.GetEdgeType(edgeId);
    /// <inheritdoc />
    public EdgeId CreateEdge(VertexId source, VertexId target, string type)
        => _core.CreateEdge(source, target, type);
    /// <inheritdoc />
    public EdgeId CreateEdge(VertexId source, VertexId target, EdgeTypeId typeId)
        => _core.CreateEdge(source, target, typeId);
    /// <inheritdoc />
    public (EdgeId Id, bool Created) MergeEdge(VertexId source, VertexId target, string type)
        => _core.MergeEdge(source, target, type);
    /// <inheritdoc />
    public void DeleteEdge(EdgeId edgeId) => _core.DeleteEdge(edgeId);
    /// <inheritdoc />
    public void SetProperty(VertexId vertexId, string key, in PropertyValue value)
        => _core.SetProperty(vertexId, key, in value);
    /// <inheritdoc />
    public void SetProperty(EdgeId edgeId, string key, in PropertyValue value)
        => _core.SetProperty(edgeId, key, in value);
    /// <inheritdoc />
    public void RemoveProperty(VertexId vertexId, string key) => _core.RemoveProperty(vertexId, key);
    /// <inheritdoc />
    public PropertyValue GetProperty(VertexId vertexId, string key) => _core.GetProperty(vertexId, key);
    /// <inheritdoc />
    public PropertyValue GetProperty(EdgeId edgeId, string key) => _core.GetProperty(edgeId, key);
    /// <inheritdoc />
    public bool HasProperty(VertexId vertexId, string key) => _core.HasProperty(vertexId, key);
    /// <inheritdoc />
    public void AddPropertyValue(VertexId vertexId, string key, in PropertyValue value)
        => _core.AddPropertyValue(vertexId, key, in value);
    /// <inheritdoc />
    public void AddPropertyValue(EdgeId edgeId, string key, in PropertyValue value)
        => _core.AddPropertyValue(edgeId, key, in value);
    /// <inheritdoc />
    public void RemovePropertyValue(VertexId vertexId, string key, in PropertyValue value)
        => _core.RemovePropertyValue(vertexId, key, in value);
    /// <inheritdoc />
    public void RemovePropertyValue(EdgeId edgeId, string key, in PropertyValue value)
        => _core.RemovePropertyValue(edgeId, key, in value);
    /// <inheritdoc />
    public PropertyValuesEnumerator GetPropertyValues(VertexId vertexId, string key)
        => _core.GetPropertyValues(vertexId, key);
    /// <inheritdoc />
    public PropertyValuesEnumerator GetPropertyValues(EdgeId edgeId, string key)
        => _core.GetPropertyValues(edgeId, key);
    /// <inheritdoc />
    public PropertyCursor EnumerateProperties(VertexId vertexId) => _core.EnumerateProperties(vertexId);
    /// <inheritdoc />
    public EdgeEnumerator EnumerateEdges(
        VertexId vertexId,
        Direction direction = Direction.Both,
        string? typeFilter = null)
        => _core.EnumerateEdges(vertexId, direction, typeFilter);
    /// <inheritdoc />
    public EntityRefEnumerator SeekIndex(string indexName, in PropertyValue key)
        => _core.SeekIndex(indexName, in key);
    /// <inheritdoc />
    public EntityRefEnumerator RangeIndex(
        string indexName,
        in PropertyValue from,
        bool fromInclusive,
        in PropertyValue to,
        bool toInclusive)
        => _core.RangeIndex(indexName, in from, fromInclusive, in to, toInclusive);
    /// <inheritdoc />
    public void SetVector(EntityKind kind, long entityId, string indexName, ReadOnlySpan<float> vector)
        => _core.SetVector(kind, entityId, indexName, vector);
    /// <inheritdoc />
    public void RemoveVector(EntityKind kind, long entityId, string indexName)
        => _core.RemoveVector(kind, entityId, indexName);
    /// <inheritdoc />
    public bool TryGetVector(EntityKind kind, long entityId, string indexName, Span<float> destination)
        => _core.TryGetVector(kind, entityId, indexName, destination);
    /// <inheritdoc />
    public NexusId CreateNexus(string type, ReadOnlySpan<NexusMember> members)
        => _core.CreateNexus(type, members);
    /// <inheritdoc />
    public NexusId CreateNexus(NexusTypeId typeId, ReadOnlySpan<NexusMember> members)
        => _core.CreateNexus(typeId, members);
    /// <inheritdoc />
    public (NexusId Id, bool Created) MergeNexus(
        string type,
        ReadOnlySpan<NexusMember> members)
        => _core.MergeNexus(type, members);
    /// <inheritdoc />
    public void DeleteNexus(NexusId nexusId) => _core.DeleteNexus(nexusId);
    /// <inheritdoc />
    public NexusMemberEnumerator GetMembers(NexusId nexusId, string? role = null)
        => _core.GetMembers(nexusId, role);
    /// <inheritdoc />
    public NexusIdEnumerator GetNexuses(VertexId vertexId, string? type = null, string? role = null)
        => _core.GetNexuses(vertexId, type, role);
    /// <inheritdoc />
    public string? GetNexusTypeName(NexusTypeId typeId) => _core.GetNexusTypeName(typeId);

    /// <inheritdoc />
    public string? GetNexusType(NexusId nexusId) => _core.GetNexusType(nexusId);
    /// <inheritdoc />
    public void SetProperty(NexusId nexusId, string key, in PropertyValue value)
        => _core.SetProperty(nexusId, key, in value);
    /// <inheritdoc />
    public PropertyValue GetProperty(NexusId nexusId, string key) => _core.GetProperty(nexusId, key);
    /// <inheritdoc />
    public bool HasProperty(NexusId nexusId, string key) => _core.HasProperty(nexusId, key);
    /// <inheritdoc />
    public void RemoveProperty(NexusId nexusId, string key) => _core.RemoveProperty(nexusId, key);
    /// <inheritdoc />
    public PropertyCursor EnumerateProperties(NexusId nexusId) => _core.EnumerateProperties(nexusId);
    /// <inheritdoc />
    public void AddPropertyValue(NexusId nexusId, string key, in PropertyValue value)
        => _core.AddPropertyValue(nexusId, key, in value);
    /// <inheritdoc />
    public void RemovePropertyValue(NexusId nexusId, string key, in PropertyValue value)
        => _core.RemovePropertyValue(nexusId, key, in value);
    /// <inheritdoc />
    public PropertyValuesEnumerator GetPropertyValues(NexusId nexusId, string key)
        => _core.GetPropertyValues(nexusId, key);
    /// <inheritdoc />
    public void Commit() => _core.Commit();
    /// <inheritdoc />
    public void Rollback() => _core.Rollback();
    /// <inheritdoc />
    public SavepointId Savepoint(string? name = null) => _core.Savepoint(name);
    /// <inheritdoc />
    public void RollbackTo(SavepointId savepoint) => _core.RollbackTo(savepoint);
    /// <inheritdoc />
    public void ReleaseSavepoint(SavepointId savepoint) => _core.ReleaseSavepoint(savepoint);
    /// <inheritdoc />
    public void OnCommitted(Action callback) => _core.OnCommitted(callback);
    /// <inheritdoc />
    public void OnRolledBack(Action callback) => _core.OnRolledBack(callback);
    /// <inheritdoc />
    public void Dispose() => _core.Dispose();

    ITransaction IReadTransactionInternal.Inner => _core.Inner;
    TransactionId IReadTransactionInternal.TransactionId => _core.TransactionId;
    TransactionUsageLease IReadTransactionInternal.EnterUsage() => _core.EnterUsage();
    IGraphAccessMethods IReadTransactionInternal.Access => _core.Access;
    IAdjacencySegmentStore? IReadTransactionInternal.AdjacencySegments => _core.AdjacencySegments;
    QueryResult IReadTransactionInternal.Execute(IPhysicalOperator plan) => _core.Execute(plan);
    IQueryCursor IReadTransactionInternal.ExecuteCursor(IPhysicalOperator plan) => _core.ExecuteCursor(plan);
    bool IReadTransactionInternal.TryColumnAggregate(
        EntityKind kind,
        string key,
        out ColumnAggregate result)
        => _core.TryColumnAggregate(kind, key, out result);
}

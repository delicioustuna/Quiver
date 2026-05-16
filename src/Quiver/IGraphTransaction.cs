using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

public interface IGraphTransaction : IDisposable, ICommitHookRegistrar
{
    TransactionId Id { get; }
    TransactionState State { get; }

    /// <summary>
    /// True when the transaction was opened as read-only.
    /// Parallel traversal operators (e.g. ParallelBfsOperator) require a read-only
    /// transaction to avoid concurrent write conflicts.
    /// </summary>
    bool IsReadOnly { get; }

    // ノード操作
    NodeId CreateNode(string label);
    NodeId CreateNode(LabelId labelId);
    void DeleteNode(NodeId nodeId);
    bool NodeExists(NodeId nodeId);

    /// <summary>
    /// GC-5: Cypher <c>MERGE (n:label {matchKey: matchValue})</c> — return the
    /// id of an existing node that matches <paramref name="label"/> and carries
    /// <paramref name="matchKey"/> equal to <paramref name="matchValue"/>; when
    /// none exists, allocate a new node, set the match property on it, and
    /// return that. <c>Created</c> distinguishes the two paths so callers can
    /// branch into <c>ON CREATE SET</c> / <c>ON MATCH SET</c> logic. The first
    /// match (by NodeId order) wins when the database holds duplicates.
    /// Equality is per-byte for String/Bytes, bit-exact for Double, and
    /// scalar-equal across the Bool/Int32/Int64 family.
    /// </summary>
    (NodeId Id, bool Created) MergeNode(string label, string matchKey, in PropertyValue matchValue);

    // リレーション操作
    RelationshipId CreateRelationship(NodeId source, NodeId target, string type);
    RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId);
    void DeleteRelationship(RelationshipId relId);

    // プロパティ操作
    void SetProperty(NodeId nodeId, string key, in PropertyValue value);
    void SetProperty(RelationshipId relId, string key, in PropertyValue value);
    void RemoveProperty(NodeId nodeId, string key);
    PropertyValue GetProperty(NodeId nodeId, string key);
    PropertyValue GetProperty(RelationshipId relId, string key);
    bool HasProperty(NodeId nodeId, string key);
    PropertyEnumerator EnumerateProperties(NodeId nodeId);

    // トラバーサル
    RelationshipEnumerator EnumerateRelationships(
        NodeId nodeId,
        Direction direction = Direction.Both,
        string? typeFilter = null);

    // インデックス書き込み (データ投入時に手動で呼ぶ)
    void IndexInsert(string indexName, string key, NodeId nodeId);
    void IndexInsert(string indexName, long key, NodeId nodeId);
    void IndexInsert(string indexName, double key, NodeId nodeId);

    // インデックス (シーク API は Operator 経由を推奨)
    NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key);
    NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive);

    // 隣接ブロックインデックス（BulkLoader buildAdjacencyIndex:true 後に利用可能）
    IAdjacencyBlockStore? AdjacencyBlocks { get; }

    // 物理プラン実行
    QueryResult Execute(IPhysicalOperator plan);
    IQueryCursor ExecuteCursor(IPhysicalOperator plan);

    void Commit();
    void Rollback();
}

public ref struct NodeIdEnumerator
{
    private IEnumerator<long>? _inner;
    private NodeId _current;

    internal NodeIdEnumerator(IEnumerable<long> source)
    {
        _inner = source.GetEnumerator();
        _current = default;
    }

    public bool MoveNext()
    {
        if (_inner == null || !_inner.MoveNext()) return false;
        _current = new NodeId(_inner.Current);
        return true;
    }

    public NodeId Current => _current;
    public void Dispose() { _inner?.Dispose(); }
}

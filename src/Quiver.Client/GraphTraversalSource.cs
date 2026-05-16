using Quiver.Client.Internal;
using Quiver.Client.Match;
using Quiver.Core;

namespace Quiver.Client;

public sealed class GraphTraversalSource
{
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;

    public GraphTraversalSource(IGraphTransaction tx, ISchemaApi schema)
    {
        _tx = tx; _schema = schema;
    }

    // ── ノード書き込み ──────────────────────────────────────────────────────
    public NodeBuilder         AddNode(string label) => new(_tx, label);
    public RelationshipBuilder AddRelationship(string type) => new(_tx, type);

    /// <summary>
    /// GC-5: Cypher <c>MERGE (n:label {matchKey: matchValue})</c>. Sugar over
    /// <see cref="IGraphTransaction.MergeNode"/>; the <c>Created</c> flag lets
    /// callers branch into ON CREATE SET / ON MATCH SET logic.
    /// </summary>
    public (NodeId Id, bool Created) MergeNode(string label, string matchKey, in Stores.PropertyValue matchValue)
        => _tx.MergeNode(label, matchKey, in matchValue);

    // ── エンティティ操作糖衣 (IGraphNode<T> ベース) ─────────────────────────
    public NodeId Insert<T>(T entity)             where T : IGraphNode<T> => T.Insert(_tx, entity);
    public NodeId InsertIndexed<T>(T entity)      where T : IGraphNode<T> => T.InsertIndexed(_tx, entity);
    public T      Load<T>(NodeId id)              where T : IGraphNode<T> => T.Load(_tx, id);
    public void   Update<T>(NodeId id, T entity)  where T : IGraphNode<T> => T.Update(_tx, id, entity);
    public void   Delete<T>(NodeId id)            where T : IGraphNode<T> => T.Delete(_tx, id);

    // ── スキャン起点 ─────────────────────────────────────────────────────────
    public GraphTraversal<NodeId> V()
    {
        var builder = new ScanBuilder();
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0);
    }

    public GraphTraversal<NodeId> V(NodeId nodeId)
    {
        var builder = new SingleNodeBuilder(nodeId);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0);
    }

    public GraphTraversal<NodeId> V(params NodeId[] nodeIds)
    {
        var builder = new MultiNodeBuilder(nodeIds);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0);
    }

    // ── 型付きスキャン起点 ────────────────────────────────────────────────────
    public TypedGraphTraversal<T> V<T>() where T : IGraphNode<T>
    {
        var inner = V().HasLabel(T.GraphLabel);
        return new TypedGraphTraversal<T>(inner, _tx, _schema);
    }

    // ── Match DSL ─────────────────────────────────────────────────────────────
    public MatchQuery Match(GraphPattern pattern) => new(_tx, _schema, pattern);

    // ── VEC-5: KNN scan source ────────────────────────────────────────────────
    /// <summary>
    /// Top-k vector search as a traversal source. Emits node ids in descending
    /// similarity order; chain <c>.HasLabel(...)</c>, <c>.Out(...)</c>, etc.
    /// to compose KNN with the rest of a query (codex_advice_3.md §6.4).
    /// </summary>
    /// <remarks>
    /// Score is not propagated; users who need raw scores should call
    /// <c>db.Vectors.KnnSearch(...)</c> directly. Index must be bound to
    /// <see cref="Core.EntityKind.Node"/> — relationship-KNN is intentionally
    /// out of scope until there is a concrete use case.
    /// </remarks>
    public GraphTraversal<NodeId> Knn(string indexName, ReadOnlySpan<float> query, int k)
    {
        var builder = new Internal.KnnNodeSourceBuilder(indexName, query, k);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0);
    }
}

public static class GraphTransactionExtensions
{
    public static GraphTraversalSource G(this IGraphTransaction tx, ISchemaApi schema)
        => new(tx, schema);
}

using GraphDb.Engine.Client.Internal;
using GraphDb.Engine.Client.Match;
using GraphDb.Engine.Core;

namespace GraphDb.Engine.Client;

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
}

public static class GraphTransactionExtensions
{
    public static GraphTraversalSource G(this IGraphTransaction tx, ISchemaApi schema)
        => new(tx, schema);
}

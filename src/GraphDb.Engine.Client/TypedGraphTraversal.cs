using System.Linq.Expressions;
using GraphDb.Engine.Client.Internal;
using GraphDb.Engine.Core;

namespace GraphDb.Engine.Client;

public sealed class TypedGraphTraversal<T> where T : IGraphNode<T>
{
    private readonly GraphTraversal<NodeId> _inner;
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;

    internal TypedGraphTraversal(GraphTraversal<NodeId> inner, IGraphTransaction tx, ISchemaApi schema)
    {
        _inner = inner; _tx = tx; _schema = schema;
    }

    // ── フィルタ ──────────────────────────────────────────────────────────────

    public TypedGraphTraversal<T> Has<TProp>(Expression<Func<T, TProp>> selector, TProp value)
    {
        var key = MemberName(selector);
        GraphTraversal<NodeId> next = value switch
        {
            string  s => _inner.Has(key, s),
            int     i => _inner.Has(key, i),
            long    l => _inner.Has(key, l),
            double  d => _inner.Has(key, d),
            bool    b => _inner.Has(key, b),
            _         => throw new NotSupportedException($"Has<{typeof(TProp).Name}> is not supported. Use Has(string key, PropertyPredicate pred) for range predicates."),
        };
        return new TypedGraphTraversal<T>(next, _tx, _schema);
    }

    public TypedGraphTraversal<T> Has<TProp>(Expression<Func<T, TProp>> selector, PropertyPredicate pred)
    {
        var key = MemberName(selector);
        return new TypedGraphTraversal<T>(_inner.Has(key, pred), _tx, _schema);
    }

    // ── トラバーサル（型なしに降格） ─────────────────────────────────────────

    public GraphTraversal<NodeId> Out(string? type = null) => _inner.Out(type);
    public GraphTraversal<NodeId> In(string? type = null)  => _inner.In(type);
    public GraphTraversal<NodeId> Both(string? type = null) => _inner.Both(type);
    public GraphTraversal<string> Values(string key)       => _inner.Values(key);

    // ── 終端 ─────────────────────────────────────────────────────────────────

    public List<T> ToList()
        => _inner.ToList().ConvertAll(id => T.Load(_tx, id));

    public List<(NodeId Id, T Entity)> ToListWithIds()
        => _inner.ToList().ConvertAll(id => (id, T.Load(_tx, id)));

    public T? First() => _inner.TryNext() is { } id ? T.Load(_tx, id) : default;

    public long Count() => _inner.Count();

    // ── ヘルパー ─────────────────────────────────────────────────────────────

    private static string MemberName<TProp>(Expression<Func<T, TProp>> expr)
    {
        if (expr.Body is MemberExpression me) return me.Member.Name;
        throw new ArgumentException("Expression must be a simple property access (e.g. p => p.Name).", nameof(expr));
    }
}

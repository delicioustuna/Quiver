using System.Linq.Expressions;
using Quiver.Client.Internal;
using Quiver.Core;

namespace Quiver.Client;

/// <summary>
/// <see cref="IGraphNode{T}"/> 実装型でフィルタ済みの型付きトラバーサル。
/// 式ツリーベースのプロパティ参照 (<c>.Has(p =&gt; p.Age, 30)</c> など) や、
/// 終端で自動的にエンティティ復元を行うヘルパを提供する。
/// </summary>
/// <typeparam name="T">対象ノード型。</typeparam>
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

    /// <summary>式ツリーで指定したプロパティが <paramref name="value"/> と等しい要素のみを通す。</summary>
    /// <typeparam name="TProp">プロパティ型 (<c>string</c> / <c>int</c> / <c>long</c> / <c>double</c> / <c>bool</c>)。</typeparam>
    /// <param name="selector">プロパティへのアクセサ式 (例: <c>p =&gt; p.Name</c>)。</param>
    /// <param name="value">比較する値。</param>
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
            _         => throw new NotSupportedException($"Has<{typeof(TProp).Name}> は未対応です。範囲述語には Has(string key, PropertyPredicate pred) を使ってください。"),
        };
        return new TypedGraphTraversal<T>(next, _tx, _schema);
    }

    /// <summary>式ツリーで指定したプロパティに任意の <see cref="PropertyPredicate"/> を適用する。</summary>
    public TypedGraphTraversal<T> Has<TProp>(Expression<Func<T, TProp>> selector, PropertyPredicate pred)
    {
        var key = MemberName(selector);
        return new TypedGraphTraversal<T>(_inner.Has(key, pred), _tx, _schema);
    }

    // ── トラバーサル（型なしに降格） ─────────────────────────────────────────

    /// <summary>外向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<NodeId> Out(string? type = null) => _inner.Out(type);

    /// <summary>内向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<NodeId> In(string? type = null)  => _inner.In(type);

    /// <summary>双方向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<NodeId> Both(string? type = null) => _inner.Both(type);

    /// <summary>型付きリレーションシップで外向に辿る。</summary>
    public GraphTraversal<NodeId> Out<TRel>() where TRel : IGraphRelationship<TRel>
        => _inner.Out<TRel>();

    /// <summary>型付きリレーションシップで内向に辿る。</summary>
    public GraphTraversal<NodeId> In<TRel>() where TRel : IGraphRelationship<TRel>
        => _inner.In<TRel>();

    /// <summary>型付きリレーションシップで双方向に辿る。</summary>
    public GraphTraversal<NodeId> Both<TRel>() where TRel : IGraphRelationship<TRel>
        => _inner.Both<TRel>();

    /// <summary>外向リレーションシップ自体を放出する。</summary>
    public GraphTraversal<RelationshipId> OutRelationships(string? type = null)  => _inner.OutRelationships(type);

    /// <summary>型付き外向リレーションシップ自体を放出する。</summary>
    public GraphTraversal<RelationshipId> OutRelationships<TRel>() where TRel : IGraphRelationship<TRel> => _inner.OutRelationships<TRel>();

    /// <summary>内向リレーションシップ自体を放出する。</summary>
    public GraphTraversal<RelationshipId> InRelationships(string? type = null)   => _inner.InRelationships(type);

    /// <summary>型付き内向リレーションシップ自体を放出する。</summary>
    public GraphTraversal<RelationshipId> InRelationships<TRel>() where TRel : IGraphRelationship<TRel>  => _inner.InRelationships<TRel>();

    /// <summary>双方向リレーションシップ自体を放出する。</summary>
    public GraphTraversal<RelationshipId> BothRelationships(string? type = null) => _inner.BothRelationships(type);

    /// <summary>型付き双方向リレーションシップ自体を放出する。</summary>
    public GraphTraversal<RelationshipId> BothRelationships<TRel>() where TRel : IGraphRelationship<TRel> => _inner.BothRelationships<TRel>();

    /// <summary>プロパティキー名でプロパティ値を取り出す。</summary>
    public GraphTraversal<string> Values(string key) => _inner.Values(key);

    /// <summary>式ツリーで指定したプロパティ値を取り出す。</summary>
    public GraphTraversal<string> Values<TProp>(Expression<Func<T, TProp>> selector)
        => _inner.Values(MemberName(selector));

    // ── 終端 ─────────────────────────────────────────────────────────────────

    /// <summary>結果ノードを <typeparamref name="T"/> インスタンスに復元してリストで返す。</summary>
    public List<T> ToList()
        => _inner.ToList().ConvertAll(id => T.Load(_tx, id));

    /// <summary>ノード ID とエンティティのペアでリスト化する。</summary>
    public List<(NodeId Id, T Entity)> ToListWithIds()
        => _inner.ToList().ConvertAll(id => (id, T.Load(_tx, id)));

    /// <summary>最初の 1 件をエンティティとして返す。結果が空のときは <see langword="default"/>。</summary>
    public T? First() => _inner.TryNext() is { } id ? T.Load(_tx, id) : default;

    /// <summary>結果件数を返す終端ステップ。</summary>
    public long Count() => _inner.Count();

    // ── ヘルパー ─────────────────────────────────────────────────────────────

    private static string MemberName<TProp>(Expression<Func<T, TProp>> expr)
    {
        if (expr.Body is MemberExpression me) return me.Member.Name;
        throw new ArgumentException("式は単純なプロパティアクセス (例: p => p.Name) でなければなりません。", nameof(expr));
    }
}

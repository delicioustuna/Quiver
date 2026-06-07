using System.Linq.Expressions;
using Quiver.Api.Internal;
using Quiver.Core;

namespace Quiver.Api;

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

    /// <summary>
    /// GC-7: C# 式ツリーによる述語フィルタ (LINQ ライク)。比較 (<c>&gt; &gt;= &lt; &lt;= == !=</c>)、
    /// <c>&amp;&amp;</c> (暗黙 AND)、同一キーの <c>||</c>、<c>StartsWith/EndsWith/Contains</c>、否定 <c>!</c> に対応する。
    /// 例: <c>.Where(p =&gt; p.Age &gt; 20 &amp;&amp; p.Name.StartsWith("A"))</c>。
    /// 対応外の式 (キー跨ぎ <c>||</c>、double 範囲など) は <see cref="NotSupportedException"/>。
    /// その場合は <see cref="Has{TProp}(Expression{Func{T, TProp}}, PropertyPredicate)"/> を使う。
    /// </summary>
    public TypedGraphTraversal<T> Where(Expression<Func<T, bool>> predicate)
        => new TypedGraphTraversal<T>(ExpressionPredicate.Apply(_inner, predicate), _tx, _schema);

    // ── トラバーサル（型なしに降格） ─────────────────────────────────────────

    /// <summary>外向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<NodeId> Out(string? type = null) => _inner.Out(type);

    /// <summary>内向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<NodeId> In(string? type = null)  => _inner.In(type);

    /// <summary>双方向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<NodeId> Both(string? type = null) => _inner.Both(type);

    /// <summary>
    /// 端点型を保持するリレーションシップ <typeparamref name="TRel"/> で外向に辿り、
    /// 終点 <typeparamref name="TTarget"/> 型の型付きトラバーサルを返す (ARCH-8 ホップ型保存)。
    /// 制約 <c>IGraphRelationship&lt;TRel, T, TTarget&gt;</c> が「現在のノード型 <typeparamref name="T"/> が
    /// <typeparamref name="TRel"/> の始点である」ことをコンパイル時に強制する。
    /// 通常は SourceGenerator 生成の糖衣 (<c>.Knows()</c> 等) を使い、明示形は escape hatch。
    /// </summary>
    public TypedGraphTraversal<TTarget> Out<TRel, TTarget>()
        where TRel : IGraphRelationship<TRel, T, TTarget>
        where TTarget : IGraphNode<TTarget>
        => new TypedGraphTraversal<TTarget>(_inner.Out(TRel.GraphType), _tx, _schema);

    /// <summary>
    /// 端点型を保持するリレーションシップ <typeparamref name="TRel"/> で内向に辿り、
    /// 始点 <typeparamref name="TSource"/> 型の型付きトラバーサルを返す (ARCH-8 ホップ型保存)。
    /// 現在のノード型 <typeparamref name="T"/> は <typeparamref name="TRel"/> の終点。
    /// </summary>
    public TypedGraphTraversal<TSource> In<TRel, TSource>()
        where TRel : IGraphRelationship<TRel, TSource, T>
        where TSource : IGraphNode<TSource>
        => new TypedGraphTraversal<TSource>(_inner.In(TRel.GraphType), _tx, _schema);

    /// <summary>型付きリレーションシップで双方向に辿る (方向が定まらないため型なしに降格)。</summary>
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

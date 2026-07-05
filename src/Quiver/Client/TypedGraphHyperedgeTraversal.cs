using System.Linq.Expressions;
using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// <see cref="IGraphHyperedge{T}"/> 実装型でフィルタ済みの型付きハイパーエッジトラバーサル。
/// 型付きノードトラバーサルからロール展開で遷移し、ロールごとのメンバー展開で
/// ふたたび型付きノードトラバーサルへ戻ることで、ホップ間でロールのノード型を保存する。
/// </summary>
/// <remarks>
/// ロール名とハイパーエッジ型は SourceGenerator が生成するロール展開糖衣
/// (ロールプロパティ名にちなむ拡張メソッド) が解決する。本型はそれらの糖衣が
/// 型なし <see cref="GraphTraversal{T}"/> の <c>Members</c> / <c>OtherMembers</c> へ
/// 委譲するための薄いラッパであり、別の実行経路を持たない。
/// </remarks>
/// <typeparam name="THyperedge">対象ハイパーエッジ型。</typeparam>
public sealed class TypedGraphHyperedgeTraversal<THyperedge> where THyperedge : IGraphHyperedge<THyperedge>
{
    private readonly GraphTraversal<HyperedgeId> _inner;
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;

    internal TypedGraphHyperedgeTraversal(GraphTraversal<HyperedgeId> inner, IGraphTransaction tx, ISchemaApi schema)
    {
        _inner = inner; _tx = tx; _schema = schema;
    }

    /// <summary>式ツリーで指定したハイパーエッジプロパティが <paramref name="value"/> と等しい要素のみを通す。</summary>
    /// <typeparam name="TProp">プロパティ型 (<c>string</c> / <c>int</c> / <c>long</c> / <c>double</c> / <c>bool</c> ほか)。</typeparam>
    /// <param name="selector">プロパティへのアクセサ式 (例: <c>f =&gt; f.Predicate</c>)。</param>
    /// <param name="value">比較する値。</param>
    public TypedGraphHyperedgeTraversal<THyperedge> Has<TProp>(Expression<Func<THyperedge, TProp>> selector, TProp value)
    {
        var key = MemberName(selector);
        GraphTraversal<HyperedgeId> next = value switch
        {
            string  s => _inner.Has(key, s),
            int     i => _inner.Has(key, i),
            long    l => _inner.Has(key, l),
            double  d => _inner.Has(key, d),
            float   f => _inner.Has(key, (double)f),
            Half    h => _inner.Has(key, (double)h),
            bool    b => _inner.Has(key, b),
            DateTime dt        => _inner.Has(key, Quiver.Storage.Records.TemporalCodec.ToUtcTicks(dt)),
            DateTimeOffset dto => _inner.Has(key, Quiver.Storage.Records.TemporalCodec.OffsetToUtcTicks(dto)),
            DateOnly d         => _inner.Has(key, Quiver.Storage.Records.TemporalCodec.ToDayNumber(d)),
            TimeOnly t         => _inner.Has(key, Quiver.Storage.Records.TemporalCodec.ToTicks(t)),
            TimeSpan ts        => _inner.Has(key, Quiver.Storage.Records.TemporalCodec.ToTicks(ts)),
            _         => throw new NotSupportedException($"Has<{typeof(TProp).Name}> は未対応です。範囲述語には Has(string key, PropertyPredicate pred) を使ってください。"),
        };
        return new TypedGraphHyperedgeTraversal<THyperedge>(next, _tx, _schema);
    }

    /// <summary>式ツリーで指定したハイパーエッジプロパティに任意の <see cref="PropertyPredicate"/> を適用する。</summary>
    public TypedGraphHyperedgeTraversal<THyperedge> Has<TProp>(Expression<Func<THyperedge, TProp>> selector, PropertyPredicate pred)
    {
        var key = MemberName(selector);
        return new TypedGraphHyperedgeTraversal<THyperedge>(_inner.Has(key, pred), _tx, _schema);
    }

    // ── ロール展開 (SourceGenerator 糖衣の委譲先) ────────────────────────────
    // ロール名はコンパイル時に糖衣側が解決し、ここへ文字列で渡す。型なし DSL の
    // Members / OtherMembers へそのまま委譲し、別の logical op を作らない。

    /// <summary>
    /// 指定ロールのメンバーノードへ展開し、<typeparamref name="TNode"/> 型を保存した
    /// ノードトラバーサルへ戻る。複数メンバーロールでも各メンバーノードを個別の行として放出し、
    /// コレクション自体は行に載せない。通常は SourceGenerator 生成の糖衣から呼ばれる。
    /// </summary>
    /// <typeparam name="TNode">ロールが束縛するノード型。</typeparam>
    /// <param name="role">展開するロール名。</param>
    public TypedGraphTraversal<TNode> MembersOf<TNode>(string role) where TNode : IGraphNode<TNode>
        => new TypedGraphTraversal<TNode>(_inner.Members(role), _tx, _schema);

    /// <summary>
    /// 指定ロールのメンバーノードから、このハイパーエッジへ到達した起点ノードを除いて展開し、
    /// <typeparamref name="TNode"/> 型を保存したノードトラバーサルへ戻る。
    /// co-membership 走査 (起点ノードと同じハイパーエッジに参加する別ノードの取得) に使う。
    /// </summary>
    /// <typeparam name="TNode">ロールが束縛するノード型。</typeparam>
    /// <param name="role">展開するロール名。</param>
    /// <exception cref="InvalidOperationException">展開元ノードを持たない起点から呼び出した場合。</exception>
    public TypedGraphTraversal<TNode> OtherMembersOf<TNode>(string role) where TNode : IGraphNode<TNode>
        => new TypedGraphTraversal<TNode>(_inner.OtherMembers(role), _tx, _schema);

    // ── トラバーサル (型なしに降格) ─────────────────────────────────────────

    /// <summary>ロールを指定せず全メンバーノードへ展開する (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<NodeId> Members(string? role = null) => _inner.Members(role);

    /// <summary>起点ノードを除いた全メンバーノードへ展開する (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<NodeId> OtherMembers(string? role = null) => _inner.OtherMembers(role);

    /// <summary>プロパティ値を取り出す (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<string> Values(string key) => _inner.Values(key);

    // ── 終端 ─────────────────────────────────────────────────────────────────

    /// <summary>結果ハイパーエッジを <typeparamref name="THyperedge"/> インスタンスに復元してリストで返す。</summary>
    public List<THyperedge> ToList()
        => _inner.ToList().ConvertAll(id => THyperedge.Load(_tx, id));

    /// <summary>ハイパーエッジ ID とエンティティのペアでリスト化する。</summary>
    public List<(HyperedgeId Id, THyperedge Entity)> ToListWithIds()
        => _inner.ToList().ConvertAll(id => (id, THyperedge.Load(_tx, id)));

    /// <summary>結果ハイパーエッジ ID をそのままリストで返す。</summary>
    public List<HyperedgeId> ToIdList() => _inner.ToList();

    /// <summary>最初の 1 件をエンティティとして返す。結果が空のときは <see langword="default"/>。</summary>
    public THyperedge? First() => _inner.TryNext() is { } id ? THyperedge.Load(_tx, id) : default;

    /// <summary>結果件数を返す終端ステップ。</summary>
    public long Count() => _inner.Count();

    // ── ヘルパー ─────────────────────────────────────────────────────────────

    private static string MemberName<TProp>(Expression<Func<THyperedge, TProp>> expr)
    {
        if (expr.Body is MemberExpression me) return me.Member.Name;
        throw new ArgumentException("式は単純なプロパティアクセス (例: f => f.Predicate) でなければなりません。", nameof(expr));
    }
}

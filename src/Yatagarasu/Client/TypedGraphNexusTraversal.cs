using System.Linq.Expressions;
using Yatagarasu.Core;

namespace Yatagarasu.Api;

/// <summary>
/// <see cref="IGraphNexus{T}"/> 実装型でフィルタ済みの型付きNexusトラバーサル。
/// 型付きVertexトラバーサルからロール展開で遷移し、ロールごとのメンバー展開で
/// ふたたび型付きVertexトラバーサルへ戻ることで、ホップ間でロールのVertex型を保存する。
/// </summary>
/// <remarks>
/// ロール名とNexus型は SourceGenerator が生成するロール展開糖衣
/// (ロールプロパティ名にちなむ拡張メソッド) が解決する。本型はそれらの糖衣が
/// 型なし <see cref="GraphTraversal{T}"/> の <c>Members</c> / <c>OtherMembers</c> へ
/// 委譲するための薄いラッパであり、別の実行経路を持たない。
/// </remarks>
/// <typeparam name="TNexus">対象Nexus型。</typeparam>
internal sealed class TypedGraphNexusTraversal<TNexus> where TNexus : IGraphNexus<TNexus>
{
    private readonly GraphTraversal<NexusId> _inner;
    private readonly IReadTransaction _tx;
    private readonly ISchemaCatalog _schema;

    internal TypedGraphNexusTraversal(GraphTraversal<NexusId> inner, IReadTransaction tx, ISchemaCatalog schema)
    {
        _inner = inner; _tx = tx; _schema = schema;
    }

    /// <summary>式ツリーで指定したNexusプロパティが <paramref name="value"/> と等しい要素のみを通す。</summary>
    /// <typeparam name="TProp">プロパティ型 (<c>string</c> / <c>int</c> / <c>long</c> / <c>double</c> / <c>bool</c> ほか)。</typeparam>
    /// <param name="selector">プロパティへのアクセサ式 (例: <c>f =&gt; f.Predicate</c>)。</param>
    /// <param name="value">比較する値。</param>
    public TypedGraphNexusTraversal<TNexus> Has<TProp>(Expression<Func<TNexus, TProp>> selector, TProp value)
    {
        var key = MemberName(selector);
        GraphTraversal<NexusId> next = value switch
        {
            string  s => _inner.Has(key, s),
            int     i => _inner.Has(key, i),
            long    l => _inner.Has(key, l),
            double  d => _inner.Has(key, d),
            float   f => _inner.Has(key, (double)f),
            Half    h => _inner.Has(key, (double)h),
            bool    b => _inner.Has(key, b),
            DateTime dt        => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.ToUtcTicks(dt)),
            DateTimeOffset dto => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.OffsetToUtcTicks(dto)),
            DateOnly d         => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.ToDayNumber(d)),
            TimeOnly t         => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.ToTicks(t)),
            TimeSpan ts        => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.ToTicks(ts)),
            _         => throw new NotSupportedException($"Has<{typeof(TProp).Name}> は未対応です。範囲述語には Has(string key, PropertyPredicate pred) を使ってください。"),
        };
        return new TypedGraphNexusTraversal<TNexus>(next, _tx, _schema);
    }

    /// <summary>式ツリーで指定したNexusプロパティに任意の <see cref="PropertyPredicate"/> を適用する。</summary>
    public TypedGraphNexusTraversal<TNexus> Has<TProp>(Expression<Func<TNexus, TProp>> selector, PropertyPredicate pred)
    {
        var key = MemberName(selector);
        return new TypedGraphNexusTraversal<TNexus>(_inner.Has(key, pred), _tx, _schema);
    }

    // ── ロール展開 (SourceGenerator 糖衣の委譲先) ────────────────────────────
    // ロール名はコンパイル時に糖衣側が解決し、ここへ文字列で渡す。型なし DSL の
    // Members / OtherMembers へそのまま委譲し、別の logical op を作らない。

    /// <summary>
    /// 指定ロールのメンバーVertexへ展開し、<typeparamref name="TVertex"/> 型を保存した
    /// Vertexトラバーサルへ戻る。複数メンバーロールでも各メンバーVertexを個別の行として放出し、
    /// コレクション自体は行に載せない。通常は SourceGenerator 生成の糖衣から呼ばれる。
    /// </summary>
    /// <typeparam name="TVertex">ロールが束縛するVertex型。</typeparam>
    /// <param name="role">展開するロール名。</param>
    public TypedGraphTraversal<TVertex> MembersOf<TVertex>(string role) where TVertex : IGraphVertex<TVertex>
        => new TypedGraphTraversal<TVertex>(_inner.Members(role), _tx, _schema);

    /// <summary>
    /// 指定ロールのメンバーVertexから、このNexusへ到達した起点Vertexを除いて展開し、
    /// <typeparamref name="TVertex"/> 型を保存したVertexトラバーサルへ戻る。
    /// co-membership 走査 (起点Vertexと同じNexusに参加する別Vertexの取得) に使う。
    /// </summary>
    /// <typeparam name="TVertex">ロールが束縛するVertex型。</typeparam>
    /// <param name="role">展開するロール名。</param>
    /// <exception cref="InvalidOperationException">展開元Vertexを持たない起点から呼び出した場合。</exception>
    public TypedGraphTraversal<TVertex> OtherMembersOf<TVertex>(string role) where TVertex : IGraphVertex<TVertex>
        => new TypedGraphTraversal<TVertex>(_inner.OtherMembers(role), _tx, _schema);

    // ── トラバーサル (型なしに降格) ─────────────────────────────────────────

    /// <summary>ロールを指定せず全メンバーVertexへ展開する (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<VertexId> Members(string? role = null) => _inner.Members(role);

    /// <summary>起点Vertexを除いた全メンバーVertexへ展開する (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<VertexId> OtherMembers(string? role = null) => _inner.OtherMembers(role);

    /// <summary>プロパティ値を取り出す (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<string> Values(string key) => _inner.Values(key);

    // ── 終端 ─────────────────────────────────────────────────────────────────

    /// <summary>結果Nexusを <typeparamref name="TNexus"/> インスタンスに復元してリストで返す。</summary>
    public List<TNexus> ToList()
        => _inner.ToList().ConvertAll(id => TNexus.Load(_tx, id));

    /// <summary>Nexus ID とエンティティのペアでリスト化する。</summary>
    public List<(NexusId Id, TNexus Entity)> ToListWithIds()
        => _inner.ToList().ConvertAll(id => (id, TNexus.Load(_tx, id)));

    /// <summary>結果Nexus ID をそのままリストで返す。</summary>
    public List<NexusId> ToIdList() => _inner.ToList();

    /// <summary>最初の 1 件をエンティティとして返す。結果が空のときは <see langword="default"/>。</summary>
    public TNexus? First() => _inner.TryNext() is { } id ? TNexus.Load(_tx, id) : default;

    /// <summary>結果件数を返す終端ステップ。</summary>
    public long Count() => _inner.Count();

    // ── ヘルパー ─────────────────────────────────────────────────────────────

    private static string MemberName<TProp>(Expression<Func<TNexus, TProp>> expr)
    {
        if (expr.Body is MemberExpression me) return me.Member.Name;
        throw new ArgumentException("式は単純なプロパティアクセス (例: f => f.Predicate) でなければなりません。", nameof(expr));
    }
}

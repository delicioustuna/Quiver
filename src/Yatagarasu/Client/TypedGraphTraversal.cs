using System.Linq.Expressions;
using Yatagarasu.Api.Internal;
using Yatagarasu.Core;
using Yatagarasu.Query.Logical;

namespace Yatagarasu.Api;

/// <summary>
/// <see cref="IGraphVertex{T}"/> 実装型でフィルタ済みの型付きトラバーサル。
/// 式ツリーベースのプロパティ参照 (<c>.Has(p =&gt; p.Age, 30)</c> など) や、
/// 終端で自動的にエンティティ復元を行うヘルパを提供する。
/// </summary>
/// <typeparam name="T">対象Vertex型。</typeparam>
internal sealed class TypedGraphTraversal<T> where T : IGraphVertex<T>
{
    private readonly GraphTraversal<VertexId> _inner;
    private readonly IReadTransaction _tx;
    private readonly ISchemaCatalog _schema;

    internal TypedGraphTraversal(GraphTraversal<VertexId> inner, IReadTransaction tx, ISchemaCatalog schema)
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
        GraphTraversal<VertexId> next = value switch
        {
            string  s => _inner.Has(key, s),
            int     i => _inner.Has(key, i),
            long    l => _inner.Has(key, l),
            double  d => _inner.Has(key, d),
            float   f => _inner.Has(key, (double)f),   // float は Double に widen 格納
            Half    h => _inner.Has(key, (double)h),   // Half も Double に widen
            bool    b => _inner.Has(key, b),
            // 日時系は格納と同じ正準 long へ (TemporalCodec 共有)。
            DateTime dt        => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.ToUtcTicks(dt)),
            DateTimeOffset dto => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.OffsetToUtcTicks(dto)),
            DateOnly d         => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.ToDayNumber(d)),
            TimeOnly t         => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.ToTicks(t)),
            TimeSpan ts        => _inner.Has(key, Yatagarasu.Storage.Records.TemporalCodec.ToTicks(ts)),
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
    /// <see cref="List{T}"/> 型 (Set cardinality) プロパティに対する包含フィルタ。
    /// <paramref name="value"/> を含む要素のみを通す。B+Tree インデックスが存在すれば利用される。
    /// </summary>
    /// <typeparam name="TElem">リストの要素型。</typeparam>
    /// <param name="selector">プロパティへのアクセサ式 (例: <c>p =&gt; p.Tags</c>)。</param>
    /// <param name="value">包含チェックする値。</param>
    public TypedGraphTraversal<T> Has<TElem>(Expression<Func<T, List<TElem>>> selector, TElem value)
    {
        var key = MemberName(selector);
        GraphTraversal<VertexId> next;
        if (typeof(TElem) == typeof(string))
            next = _inner.Has(key, (string)(object)value!);
        else if (typeof(TElem) == typeof(int))
            next = _inner.Has(key, (int)(object)value!);
        else if (typeof(TElem) == typeof(long))
            next = _inner.Has(key, (long)(object)value!);
        else if (typeof(TElem) == typeof(double))
            next = _inner.Has(key, (double)(object)value!);
        else if (typeof(TElem) == typeof(bool))
            next = _inner.Has(key, (bool)(object)value!);
        else
            throw new NotSupportedException($"Has<List<{typeof(TElem).Name}>> is not supported.");
        return new TypedGraphTraversal<T>(next, _tx, _schema);
    }

    /// <summary>
    /// C# 式ツリーによる述語フィルタ (LINQ ライク)。比較 (<c>&gt; &gt;= &lt; &lt;= == !=</c>)、
    /// <c>&amp;&amp;</c> (暗黙 AND)、同一キーの <c>||</c>、<c>StartsWith/EndsWith/Contains</c>、否定 <c>!</c> に対応する。
    /// 例: <c>.Where(p =&gt; p.Age &gt; 20 &amp;&amp; p.Name.StartsWith("A"))</c>。
    /// 対応外の式 (キー跨ぎ <c>||</c>、double 範囲など) は <see cref="NotSupportedException"/>。
    /// その場合は <see cref="Has{TProp}(Expression{Func{T, TProp}}, PropertyPredicate)"/> を使う。
    /// </summary>
    public TypedGraphTraversal<T> Where(Expression<Func<T, bool>> predicate)
        => new TypedGraphTraversal<T>(ExpressionPredicate.Apply(_inner, predicate), _tx, _schema);

    /// <summary>
    /// エッジ述語付きの型保存ホップ。<typeparamref name="TEdge"/> エッジを
    /// <paramref name="edgeFilter"/> (式ツリー述語) で絞り込んでから終点 <typeparamref name="TTarget"/> へ辿る。
    /// 通常は SourceGenerator 生成の糖衣 (<c>.Knows(e =&gt; e.Since == "2024-01")</c>) から呼ばれる。
    /// エッジプロパティ述語は <see cref="ExpressionPredicate"/> がEdge用に構築する。
    /// </summary>
    public TypedGraphTraversal<TTarget> OutWhere<TEdge, TTarget>(Expression<Func<TEdge, bool>> edgeFilter)
        where TEdge : IGraphEdge<TEdge, T, TTarget>
        where TTarget : IGraphVertex<TTarget>
    {
        var edges = _inner.OutEdges(TEdge.GraphType);
        var filtered = ExpressionPredicate.Apply(edges, edgeFilter);
        return new TypedGraphTraversal<TTarget>(filtered.TargetVertex(), _tx, _schema);
    }

    // ── トラバーサル（型なしに降格） ─────────────────────────────────────────

    /// <summary>外向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<VertexId> Out(string? type = null) => _inner.Out(type);

    /// <summary>内向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<VertexId> In(string? type = null)  => _inner.In(type);

    /// <summary>双方向に辿る (型なし <see cref="GraphTraversal{T}"/> に降格)。</summary>
    public GraphTraversal<VertexId> Both(string? type = null) => _inner.Both(type);

    /// <summary>
    /// 端点型を保持するEdge <typeparamref name="TEdge"/> で外向に辿り、
    /// 終点 <typeparamref name="TTarget"/> 型の型付きトラバーサルを返す (ホップ間で型を保存)。
    /// 制約 <c>IGraphEdge&lt;TEdge, T, TTarget&gt;</c> が「現在のVertex型 <typeparamref name="T"/> が
    /// <typeparamref name="TEdge"/> の始点である」ことをコンパイル時に強制する。
    /// 通常は SourceGenerator 生成の糖衣 (<c>.Knows()</c> 等) を使い、明示形は escape hatch。
    /// </summary>
    public TypedGraphTraversal<TTarget> Out<TEdge, TTarget>()
        where TEdge : IGraphEdge<TEdge, T, TTarget>
        where TTarget : IGraphVertex<TTarget>
        => new TypedGraphTraversal<TTarget>(_inner.Out(TEdge.GraphType), _tx, _schema);

    /// <summary>
    /// 端点型を保持するEdge <typeparamref name="TEdge"/> で内向に辿り、
    /// 始点 <typeparamref name="TSource"/> 型の型付きトラバーサルを返す (ホップ間で型を保存)。
    /// 現在のVertex型 <typeparamref name="T"/> は <typeparamref name="TEdge"/> の終点。
    /// </summary>
    public TypedGraphTraversal<TSource> In<TEdge, TSource>()
        where TEdge : IGraphEdge<TEdge, TSource, T>
        where TSource : IGraphVertex<TSource>
        => new TypedGraphTraversal<TSource>(_inner.In(TEdge.GraphType), _tx, _schema);

    /// <summary>型付きEdgeで双方向に辿る (方向が定まらないため型なしに降格)。</summary>
    public GraphTraversal<VertexId> Both<TEdge>() where TEdge : IGraphEdge<TEdge>
        => _inner.Both<TEdge>();

    /// <summary>
    /// 現在のVertexが参加する <typeparamref name="TNexus"/> 型のNexusへ展開し、
    /// 型付きNexusトラバーサルを返す (ホップ間で型を保存)。
    /// <paramref name="role"/> を指定すると、現在のVertexがそのロールで参加する
    /// Nexusだけに絞り込む。
    /// 通常は SourceGenerator 生成の糖衣 (ロールプロパティ名にちなむ拡張メソッド) を使い、
    /// 明示形は escape hatch。
    /// </summary>
    /// <typeparam name="TNexus">対象Nexus型。</typeparam>
    /// <param name="role">現在のVertexが担うロール名。null は全ロール。</param>
    public TypedGraphNexusTraversal<TNexus> Nexuses<TNexus>(string? role = null)
        where TNexus : IGraphNexus<TNexus>
        => new TypedGraphNexusTraversal<TNexus>(
            _inner.Nexuses(TNexus.GraphType, role), _tx, _schema);

    /// <summary>外向Edge自体を放出する。</summary>
    public GraphTraversal<EdgeId> OutEdges(string? type = null)  => _inner.OutEdges(type);

    /// <summary>型付き外向Edge自体を放出する。</summary>
    public GraphTraversal<EdgeId> OutEdges<TEdge>() where TEdge : IGraphEdge<TEdge> => _inner.OutEdges<TEdge>();

    /// <summary>内向Edge自体を放出する。</summary>
    public GraphTraversal<EdgeId> InEdges(string? type = null)   => _inner.InEdges(type);

    /// <summary>型付き内向Edge自体を放出する。</summary>
    public GraphTraversal<EdgeId> InEdges<TEdge>() where TEdge : IGraphEdge<TEdge>  => _inner.InEdges<TEdge>();

    /// <summary>双方向Edge自体を放出する。</summary>
    public GraphTraversal<EdgeId> BothEdges(string? type = null) => _inner.BothEdges(type);

    /// <summary>型付き双方向Edge自体を放出する。</summary>
    public GraphTraversal<EdgeId> BothEdges<TEdge>() where TEdge : IGraphEdge<TEdge> => _inner.BothEdges<TEdge>();

    /// <summary>プロパティキー名でプロパティ値を取り出す。</summary>
    public GraphTraversal<string> Values(string key) => _inner.Values(key);

    /// <summary>式ツリーで指定したプロパティ値を取り出す。</summary>
    public GraphTraversal<string> Values<TProp>(Expression<Func<T, TProp>> selector)
        => _inner.Values(MemberName(selector));

    /// <summary>式ツリーで指定した <see cref="float"/>[] プロパティ値を取り出す。</summary>
    public GraphTraversal<float[]> Values(Expression<Func<T, float[]>> selector)
        => _inner.ValuesFloatArray(MemberName(selector));

    /// <summary>
    /// Set cardinality プロパティの全値を <see cref="List{TElem}"/> として取り出す。
    /// 各Vertexに対しread transactionの複数値プロパティ列挙を呼び、
    /// 要素を collect して返す。
    /// </summary>
    /// <typeparam name="TElem">リストの要素型。</typeparam>
    /// <param name="selector">プロパティへのアクセサ式 (例: <c>p =&gt; p.Tags</c>)。</param>
    public GraphTraversal<List<TElem>> Values<TElem>(Expression<Func<T, List<TElem>>> selector)
    {
        var key = MemberName(selector);
        var tx = _tx;
        var entityCol = _inner._entityColumn;
        return new GraphTraversal<List<TElem>>(
            tx, _inner._schema, _inner._plan,
            row =>
            {
                var vertexId = row.GetVertexId(entityCol);
                var list = new List<TElem>();
                var e = tx.GetPropertyValues(vertexId, key);
                while (e.MoveNext())
                {
                    TElem val;
                    if (typeof(TElem) == typeof(string))
                        val = (TElem)(object)System.Text.Encoding.UTF8.GetString(e.Current.Utf8StringValue);
                    else if (typeof(TElem) == typeof(int))
                        val = (TElem)(object)e.Current.Int32Value;
                    else if (typeof(TElem) == typeof(long))
                        val = (TElem)(object)e.Current.Int64Value;
                    else if (typeof(TElem) == typeof(double))
                        val = (TElem)(object)e.Current.DoubleValue;
                    else if (typeof(TElem) == typeof(bool))
                        val = (TElem)(object)e.Current.BoolValue;
                    else
                        throw new NotSupportedException($"Values<List<{typeof(TElem).Name}>> is not supported.");
                    list.Add(val);
                }
                e.Dispose();
                return list;
            },
            entityCol,
            _inner._aliases,
            _inner._stats);
    }

    // ── ダイアディック演算子ステップ ──────────────────────────────────

    /// <summary>
    /// 上流の候補Vertexに対し、<paramref name="selector"/> で指定した <c>float[]</c> プロパティの
    /// 格納ベクトル (a) と <paramref name="b"/> を <typeparamref name="TOp"/> で評価し、
    /// スコア降順で上位 <paramref name="k"/> 件を放出する。
    /// <para>
    /// <paramref name="oversample"/> が <c>null</c> (既定) なら全候補を brute-force スコアリングする。
    /// 正の整数を指定すると、インデックスの組み込み距離で HNSW から <c>k × oversample</c> 件を
    /// プリフィルタし、その結果のみをカスタム演算子でリランクする (近似)。
    /// </para>
    /// </summary>
    /// <typeparam name="TOp">
    /// <see cref="IDyadicOperator{TResult}"/> を実装する <see langword="struct"/>。
    /// <see cref="DotProductOp"/> / <see cref="CosineSimilarityOp"/> / <see cref="EuclideanDistanceOp"/>
    /// またはユーザー定義型。
    /// </typeparam>
    /// <param name="selector">候補Vertexから <c>float[]</c> プロパティを取得するアクセサ式。</param>
    /// <param name="b">スコアリング対象のクエリベクトル。</param>
    /// <param name="regions">演算対象の部分領域。<c>null</c> で全域。</param>
    /// <param name="k">返す上位件数。</param>
    /// <param name="oversample">HNSW プリフィルタの倍率。<c>null</c> で brute-force。</param>
    public TypedGraphTraversal<T> ApplyDyadic<TOp>(
        Expression<Func<T, float[]>> selector,
        float[] b,
        Range[]? regions = null,
        int k = int.MaxValue,
        int? oversample = null)
        where TOp : struct, IDyadicOperator<float>
    {
        ArgumentNullException.ThrowIfNull(b);
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        if (oversample is <= 0) throw new ArgumentOutOfRangeException(nameof(oversample), oversample, "oversample must be positive.");

        var propertyName = MemberName(selector);
        var op = new ApplyDyadicOp(
            _inner._plan,
            typeof(TOp),
            propertyName,
            propertyName,
            b.ToArray(),
            null,
            regions?.ToArray(),
            k,
            CreateDyadicScorer<TOp>(),
            oversample);
        return new TypedGraphTraversal<T>(_inner.ApplyDyadicInternal(op), _tx, _schema);
    }

    /// <summary>
    /// 上流の候補Vertexに対しダイアディック演算子でスコアリングする。
    /// このオーバーロードは <paramref name="b"/> をトラバーサルで受け取り、
    /// クエリ Open 時に 1 回だけ評価する (uncorrelated sub-query)。
    /// </summary>
    /// <typeparam name="TOp">
    /// <see cref="IDyadicOperator{TResult}"/> を実装する <see langword="struct"/>。
    /// </typeparam>
    /// <param name="selector">候補Vertexから <c>float[]</c> プロパティを取得するアクセサ式。</param>
    /// <param name="b">参照ベクトルを返すトラバーサル (Open 時に 1 回だけ評価)。</param>
    /// <param name="regions">演算対象の部分領域。<c>null</c> で全域。</param>
    /// <param name="k">返す上位件数。</param>
    /// <param name="oversample">HNSW プリフィルタの倍率。<c>null</c> で brute-force。</param>
    public TypedGraphTraversal<T> ApplyDyadic<TOp>(
        Expression<Func<T, float[]>> selector,
        GraphTraversal<float[]> b,
        Range[]? regions = null,
        int k = int.MaxValue,
        int? oversample = null)
        where TOp : struct, IDyadicOperator<float>
    {
        ArgumentNullException.ThrowIfNull(b);
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        if (oversample is <= 0) throw new ArgumentOutOfRangeException(nameof(oversample), oversample, "oversample must be positive.");

        var propertyName = MemberName(selector);
        var op = new ApplyDyadicOp(
            _inner._plan,
            typeof(TOp),
            propertyName,
            propertyName,
            null,
            b._plan,
            regions?.ToArray(),
            k,
            CreateDyadicScorer<TOp>(),
            oversample);
        return new TypedGraphTraversal<T>(_inner.ApplyDyadicInternal(op), _tx, _schema);
    }

    private static DyadicScoreFunc CreateDyadicScorer<TOp>() where TOp : struct, IDyadicOperator<float>
    {
        return (a, b, regions) =>
        {
            TOp op = default;
            return op.Invoke(a, b, regions);
        };
    }

    // ── 終端 ─────────────────────────────────────────────────────────────────

    /// <summary>結果Vertexを <typeparamref name="T"/> インスタンスに復元してリストで返す。</summary>
    public List<T> ToList()
        => _inner.ToList().ConvertAll(id => T.Load(_tx, id));

    /// <summary>Vertex ID とエンティティのペアでリスト化する。</summary>
    public List<(VertexId Id, T Entity)> ToListWithIds()
        => _inner.ToList().ConvertAll(id => (id, T.Load(_tx, id)));

    /// <summary>最初の 1 件をエンティティとして返す。結果が空のときは <see langword="default"/>。</summary>
    public T? First() => _inner.TryNext() is { } id ? T.Load(_tx, id) : default;

    /// <summary>結果件数を返す終端ステップ。</summary>
    public long Count() => _inner.Count();

    // ── 集合 write シンク (AddEdge / MergeEdge) 向け internal アクセサ ───────────
    // 拡張は別クラスのため private フィールドへ届かない。ID のみで足りる経路は
    // エンティティ復元を避けるため ToListWithIds とは別に ID 列だけを返す。

    internal IReadTransaction Transaction => _tx;

    internal List<VertexId> MaterializeIds() => _inner.ToList();

    // ── ヘルパー ─────────────────────────────────────────────────────────────

    private static string MemberName<TProp>(Expression<Func<T, TProp>> expr)
    {
        if (expr.Body is MemberExpression me) return me.Member.Name;
        throw new ArgumentException("式は単純なプロパティアクセス (例: p => p.Name) でなければなりません。", nameof(expr));
    }
}

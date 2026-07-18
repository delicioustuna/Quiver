using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// <see cref="GraphTraversalSource"/> から派生し、
/// <see cref="HasLabel"/> / <see cref="Has"/> / <see cref="Out"/> / <see cref="Where"/> / <see cref="Limit"/> / <see cref="OrderBy"/>
/// などのステップをチェーンして終端 (<see cref="ToList"/> / <see cref="Next"/> /
/// <see cref="AsCursor"/> 等) で実行する。
/// </summary>
/// <typeparam name="T">現在のチェーンが放出する要素の型 (典型的には <see cref="VertexId"/> や <see cref="EdgeId"/>)。</typeparam>
/// <remarks>
/// インスタンスは不変。各ステップは新しいインスタンスを返すため、中間結果を変数に保持して
/// 分岐させても副作用は発生しない。所属トランザクションの境界を越えて利用しないこと。
/// </remarks>
public sealed class GraphTraversal<T>
{
    internal readonly IReadTransaction _tx;
    internal readonly ISchemaCatalog _schema;
    internal readonly LogicalOp _plan;
    internal readonly Func<QueryRow, T> _projection;
    internal readonly int _entityColumn;
    // エイリアス名 → 列インデックスのマップ。チェーン上で一度もエイリアスが
    // バインドされていない (一般的なケース) 場合は null。不変として扱い、丸ごと
    // 差し替える運用。インプレース変更は行わない。
    internal readonly Dictionary<string, int>? _aliases;
    // エンティティ alias の種別。Select<TEntity>(alias) が異なる ID 型で同じ
    // 64-bit payload を読み違えないため、列位置とは別に保持する。
    internal readonly Dictionary<string, EntityKind>? _aliasEntityKinds;
    // 任意で注入された GraphStats。KNN push-down 時に label cardinality が高ければ
    // vector-first にフォールバックさせる。null のときは構造ヒントのみで判定する。
    internal readonly GraphStats? _stats;
    // vertex -> nexus 展開の起点列。公開 alias とは異なり OtherMembers 専用で、
    // Members による通常展開やタプル形状のリセット時には破棄する。
    internal readonly int? _hiddenNexusOriginColumn;

    // この列が保持するエンティティ種別。ID 型に応じて述語が読むプロパティストアを切り替える。
    private static readonly PredicateEntity EntityKindForT =
        typeof(T) == typeof(EdgeId) ? PredicateEntity.Edge :
        typeof(T) == typeof(NexusId) ? PredicateEntity.Nexus :
        PredicateEntity.Vertex;

    internal GraphTraversal(
        IReadTransaction tx,
        ISchemaCatalog schema,
        LogicalOp plan,
        Func<QueryRow, T> projection,
        int entityColumn,
        Dictionary<string, int>? aliases = null,
        GraphStats? stats = null,
        int? hiddenNexusOriginColumn = null,
        Dictionary<string, EntityKind>? aliasEntityKinds = null)
    {
        _tx = tx; _schema = schema; _plan = plan; _projection = projection;
        _entityColumn = entityColumn;
        _aliases = (aliases is { Count: > 0 }) ? aliases : null;
        _aliasEntityKinds = (aliasEntityKinds is { Count: > 0 }) ? aliasEntityKinds : null;
        _stats = stats;
        _hiddenNexusOriginColumn = hiddenNexusOriginColumn;
    }

    /// <summary>同じエイリアスセットを引き継いだ後続トラバーサルを構築する内部ヘルパ。</summary>
    private GraphTraversal<U> Chain<U>(LogicalOp plan, Func<QueryRow, U> projection, int entityColumn)
        => new(
            _tx, _schema, plan, projection, entityColumn, _aliases, _stats,
            _hiddenNexusOriginColumn, _aliasEntityKinds);

    /// <summary>alias を持ち越さない (= タプル形状をリセットする) 新規 traversal を構築する内部ヘルパ。stats だけは引き継ぐ。</summary>
    private GraphTraversal<U> Rebase<U>(LogicalOp plan, Func<QueryRow, U> projection, int entityColumn, Dictionary<string, int>? aliases = null)
        => new(
            _tx, _schema, plan, projection, entityColumn, aliases, _stats,
            aliasEntityKinds: aliases is null ? null : _aliasEntityKinds);

    /// <summary>論理プランを最適化 (KNN 押し下げ等) してから物理オペレータへ落とす。</summary>
    private IPhysicalOperator Compile() => CompilePlan(_plan);

    /// <summary>テスト用 — 現在の論理プランに optimizer を適用した結果を公開する (KNN 押し下げ判定の検証)。</summary>
    internal LogicalOp Optimized() => LogicalOptimizer.Optimize(_plan, _stats, _schema);

    /// <summary>任意の論理プランを最適化 + 物理化する共通ヘルパ (集約 row path 用)。</summary>
    private IPhysicalOperator CompilePlan(LogicalOp plan)
        => PhysicalPlanner.Plan(LogicalOptimizer.Optimize(plan, _stats, _schema), _schema);

    /// <summary>
    /// <paramref name="label"/> ラベル名と等しい要素のみを通す。
    /// </summary>
    /// <param name="label">ラベル名</param>
    // 起点が <c>AllVerticesScan</c> の場合は <c>VertexByLabelScan</c> に置き換える最適化を行い、
    // それ以外はラベル述語のフィルタとして連結する。
    public GraphTraversal<VertexId> HasLabel(string label)
    {
        LogicalOp next;
        if (_plan is ScanOp { Kind: EntityKind.Vertex })
            next = new ScanOp(EntityKind.Vertex, _schema.ResolveLabel(label));
        else
        {
            var labelId = _schema.ResolveLabel(label);
            var col = _entityColumn;
            next = new FilterOp(_plan, _ => new LabelPredicate(labelId, col));
        }
        return new GraphTraversal<VertexId>(
            _tx, _schema, next, row => row.GetVertexId(_entityColumn),
            next.CurrentEntityColumn, _aliases, _stats,
            _hiddenNexusOriginColumn, _aliasEntityKinds);
    }

    /// <summary>
    /// pure-filter を plan に積む共通ヘルパ。<paramref name="factoryWithCol"/> は filter を適用する
    /// 対象列番号を受け取り、predicate ファクトリを返す。KNN 押し下げ (candidate-side rewrite) は
    /// 終端で <see cref="LogicalOptimizer"/> が <see cref="FilterOp"/> 連鎖から再構成する。
    /// </summary>
    private GraphTraversal<T> ApplyPureFilter(Func<int, Func<ISchemaCatalog, IPredicate>> factoryWithCol)
        => Chain(new FilterOp(_plan, factoryWithCol(_entityColumn)), _projection, _entityColumn);

    /// <summary>プロパティ <paramref name="key"/> が文字列 <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, string value)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        return ApplyPureFilter(col => _ => new PropertyEqStringPredicate(col, keyId, value) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="int"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, int value)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        var pred  = P.Eq((long)value);
        return ApplyPureFilter(col => _ => new PropertyInt64Predicate(col, keyId, pred) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="long"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, long value)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        var pred  = P.Eq(value);
        return ApplyPureFilter(col => _ => new PropertyInt64Predicate(col, keyId, pred) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="double"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, double value)
    {
        var keyId   = _schema.ResolvePropertyKey(key);
        var encoded = BitConverter.DoubleToInt64Bits(value);
        return ApplyPureFilter(col => _ => new PropertyDoublePredicate(col, keyId, encoded) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="bool"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, bool value)
    {
        var keyId  = _schema.ResolvePropertyKey(key);
        var scalar = value ? 1L : 0L;
        return ApplyPureFilter(col => _ => new PropertyBoolPredicate(col, keyId, scalar) { Entity = EntityKindForT });
    }

    /// <summary>
    /// プロパティ <paramref name="key"/> が <see cref="PropertyPredicate"/> に適応する要素のみを通す。
    /// </summary>
    public GraphTraversal<T> Has(string key, PropertyPredicate pred)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        return ApplyPureFilter(col => _ => PredicateDispatch.Build(col, keyId, pred, EntityKindForT));
    }

    /// <summary>外向きにEdgeを辿り、接続された隣接Vertexを返す。</summary>
    public GraphTraversal<VertexId> Out(string? type = null) => Expand(Direction.Outgoing, type);

    /// <summary>外向きにEdgeを辿り、接続された隣接Vertexを返す。</summary>
    public GraphTraversal<VertexId> Out<TEdge>() where TEdge : IGraphEdge<TEdge> => Out(TEdge.GraphType);

    /// <summary>内向きにEdgeを辿り、接続された隣接Vertexを返す。</summary>
    public GraphTraversal<VertexId> In(string? type = null) => Expand(Direction.Incoming, type);

    /// <summary>内向きにEdgeを辿り、接続された隣接Vertexを返す。</summary>
    public GraphTraversal<VertexId> In<TEdge>() where TEdge : IGraphEdge<TEdge> => In(TEdge.GraphType);

    /// <summary>向きを指定せずEdgeを辿り、接続された隣接Vertexを返す。</summary>
    public GraphTraversal<VertexId> Both(string? type = null) => Expand(Direction.Both, type);

    /// <summary>向きを指定せずEdgeを辿り、接続された隣接Vertexを返す。</summary>
    public GraphTraversal<VertexId> Both<TEdge>() where TEdge : IGraphEdge<TEdge> => Both(TEdge.GraphType);

    private GraphTraversal<VertexId> Expand(Direction direction, string? type)
    {
        // エイリアスが生きているときは展開を通してそれらをコピーし、
        // 下流の .Select(alias) が元のエンティティを引けるようにする。
        // エイリアスが無ければ fast path (余分列なし) と等価。
        // _entityColumn を明示渡しすることで、.Select(alias).Out(...) が
        // 直近 plan の出力ではなく pin された列から展開できる。
        if (_aliases is null)
        {
            var fast = new ExpandOp(_plan, _entityColumn, direction, type, ExpandOutputMode.NeighborOnly, null);
            return Rebase<VertexId>(fast, row => row.GetVertexId(fast.CurrentEntityColumn), fast.CurrentEntityColumn);
        }

        var (carry, newAliases) = RemapForExpand(baseColumnCount: 1);
        var expand = new ExpandOp(_plan, _entityColumn, direction, type, ExpandOutputMode.NeighborOnly, carry);
        return Rebase<VertexId>(expand, row => row.GetVertexId(0), 0, newAliases);
    }

    /// <summary>外向きEdgeを返す。</summary>
    public GraphTraversal<EdgeId> OutEdges(string? type = null) => ExpandEdge(Direction.Outgoing, type);

    /// <summary>外向きEdgeを返す。</summary>
    public GraphTraversal<EdgeId> OutEdges<TEdge>() where TEdge : IGraphEdge<TEdge> => OutEdges(TEdge.GraphType);

    /// <summary>内向きEdgeを返す。</summary>
    public GraphTraversal<EdgeId> InEdges(string? type = null) => ExpandEdge(Direction.Incoming, type);

    /// <summary>内向きEdgeを返す。</summary>
    public GraphTraversal<EdgeId> InEdges<TEdge>() where TEdge : IGraphEdge<TEdge> => InEdges(TEdge.GraphType);

    /// <summary>双方向Edgeを返す。</summary>
    public GraphTraversal<EdgeId> BothEdges(string? type = null) => ExpandEdge(Direction.Both, type);

    /// <summary>双方向Edgeを返す。</summary>
    public GraphTraversal<EdgeId> BothEdges<TEdge>() where TEdge : IGraphEdge<TEdge> => BothEdges(TEdge.GraphType);

    private GraphTraversal<EdgeId> ExpandEdge(Direction direction, string? type)
    {
        if (_aliases is null)
        {
            var fast = new ExpandOp(_plan, _entityColumn, direction, type, ExpandOutputMode.NeighborAndEdge, null);
            return Rebase<EdgeId>(fast, row => row.GetEdgeId(0), 0);
        }

        // NeighborAndEdge は 2 列 (edge@0, neighbor@1) を放出する。連鎖する
        // .SourceVertex() / .TargetVertex() のための「カレント」列は 0 (edge) のままなので、
        // carry は 2 から始まる。
        var (carry, newAliases) = RemapForExpand(baseColumnCount: 2);
        var e = new ExpandOp(_plan, _entityColumn, direction, type, ExpandOutputMode.NeighborAndEdge, carry);
        return Rebase<EdgeId>(e, row => row.GetEdgeId(0), 0, newAliases);
    }

    /// <summary>
    /// Out/In/Both と OutEdges/InEdges/BothEdges の共通ヘルパ。
    /// 持ち越し対象の上流列リスト (重複排除 + ソート済み) と、新しい末尾位置を指す
    /// 書き換え済みのエイリアスマップを返す。
    /// </summary>
    private (int[] carry, Dictionary<string, int> newAliases) RemapForExpand(int baseColumnCount)
    {
        // 重複排除 + ソートにより、エイリアスから新列への対応を決定的にする。
        var carry = new SortedSet<int>(_aliases!.Values).ToArray();
        var newAliases = new Dictionary<string, int>(_aliases.Count);
        foreach (var (label, oldCol) in _aliases)
        {
            int idx = Array.IndexOf(carry, oldCol);
            newAliases[label] = baseColumnCount + idx;
        }
        return (carry, newAliases);
    }

    /// <summary>
    /// サブトラバーサル条件でフィルタされた要素のみを通す。(WHERE EXISTS)
    /// </summary>
    /// <param name="innerTraversal">外側の現在エンティティを起点とする内部トラバーサル</param>
    // (Cypher の <c>WHERE EXISTS{...}</c> 相当)。
    // 例: <c>.Where(t =&gt; t.Out("KNOWS"))</c> — KNOWS エッジを持つVertexのみを通す。
    public GraphTraversal<T> Where(Func<SubTraversal, SubTraversal> innerTraversal)
    {
        var capturedInner = innerTraversal;
        return ApplyPureFilter(col => s =>
        {
            var probe = new CorrelatedInputOperator();
            var seed = new CorrelatedInputOp(probe);
            var start = new SubTraversal(probe, seed, s, 0);
            return capturedInner(start).BuildExistsPredicate(col);
        });
    }

    /// <summary>
    /// サブトラバーサル条件でフィルタされた要素のみを通す。(WHERE NOT EXISTS)
    /// </summary>
    /// <param name="innerTraversal">外側の現在エンティティを起点とする内部トラバーサル</param>
    // Cypher の WHERE NOT EXISTS{...} 相当。
    // 例: .Not(t => t.Out("KNOWS")) — KNOWS エッジを持たないVertexのみを通す。
    public GraphTraversal<T> Not(Func<SubTraversal, SubTraversal> innerTraversal)
    {
        var capturedInner = innerTraversal;
        return ApplyPureFilter(col => s =>
        {
            var probe = new CorrelatedInputOperator();
            var seed = new CorrelatedInputOp(probe);
            var start = new SubTraversal(probe, seed, s, 0);
            return capturedInner(start).BuildNotExistsPredicate(col);
        });
    }

    // ── 存在チェック ────────────────────────────────────────────────

    /// <summary>プロパティ <paramref name="key"/> を保持する要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        return ApplyPureFilter(col => _ => new PropertyExistsPredicate(col, keyId, mustExist: true) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> を持たない要素のみを通す。</summary>
    public GraphTraversal<T> HasNot(string key)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        return ApplyPureFilter(col => _ => new PropertyExistsPredicate(col, keyId, mustExist: false) { Entity = EntityKindForT });
    }

    /// <summary>
    /// プロパティ <paramref name="key"/> を保持しない要素のみを通す。
    /// </summary>
    /// <remarks>
    /// <see cref="HasNot(string)"/> の糖衣構文。
    /// </remarks>
    // Cypher の <c>IS NULL</c> — 
    public GraphTraversal<T> IsNull(string key) => HasNot(key);

    /// <summary>
    /// プロパティ <paramref name="key"/> を保持する要素のみを通す。
    /// </summary>
    /// <remarks>
    /// <see cref="Has(string)"/> の糖衣構文。
    /// </remarks>
    // Cypher の <c>IS NOT NULL</c> — 
    public GraphTraversal<T> IsNotNull(string key) => Has(key);

    // ── トラバーサルレベルの真偽結合 ────────────────────────────

    /// <summary>
    /// すべてのサブトラバーサルが少なくとも 1行を生成する要素のみを通す。
    /// </summary>
    public GraphTraversal<T> And(params Func<SubTraversal, SubTraversal>[] traversals)
    {
        if (traversals is null || traversals.Length == 0)
            throw new ArgumentException("And には少なくとも 1 つのサブトラバーサルが必要です。", nameof(traversals));
        return CombineSubTraversals(traversals, useOr: false);
    }

    /// <summary>いずれかのサブトラバーサルがマッチする要素のみを通す。</summary>
    public GraphTraversal<T> Or(params Func<SubTraversal, SubTraversal>[] traversals)
    {
        if (traversals is null || traversals.Length == 0)
            throw new ArgumentException("Or には少なくとも 1 つのサブトラバーサルが必要です。", nameof(traversals));
        return CombineSubTraversals(traversals, useOr: true);
    }

    private GraphTraversal<T> CombineSubTraversals(Func<SubTraversal, SubTraversal>[] traversals, bool useOr)
    {
        var captured = traversals;
        return ApplyPureFilter(col => s =>
        {
            var inners = new IPredicate[captured.Length];
            for (int i = 0; i < captured.Length; i++)
            {
                var probe = new CorrelatedInputOperator();
                var seed = new CorrelatedInputOp(probe);
                var start = new SubTraversal(probe, seed, s, 0);
                inners[i] = captured[i](start).BuildExistsPredicate(col);
            }
            return useOr ? new OrPredicate(inners) : new AndPredicate(inners);
        });
    }

    // ── ページネーション ─────────────────────────────────────────────────────

    /// <summary>最大 <paramref name="n"/> 件まで放出する</summary>
    public GraphTraversal<T> Limit(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        // KNN 直上の Limit は LogicalOptimizer の KnnLimitPushdown が K を min(K,n) に縮める。
        return Chain(new LimitOp(_plan, n, Skip: 0), _projection, _entityColumn);
    }

    /// <summary>先頭 <paramref name="n"/> 件をスキップしてから放出を開始する。</summary>
    public GraphTraversal<T> Skip(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        return Chain(new LimitOp(_plan, long.MaxValue, Skip: n), _projection, _entityColumn);
    }

    /// <summary>半開区間 <c>[from, to)</c> のウィンドウを放出する。</summary>
    public GraphTraversal<T> Range(long from, long to)
    {
        if (from < 0 || to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "0 <= from <= to を満たす必要があります。");
        return Chain(new LimitOp(_plan, to - from, Skip: from), _projection, _entityColumn);
    }

    // ── 終端 / 存在判定 ──────────────────────────────────────

    /// <summary>トラバーサルが少なくとも 1件の要素が存在するか。</summary>
    public bool HasNext()
    {
        using var cursor = AsCursor();
        return cursor.MoveNext();
    }

    // ── ラベル射影 ────────────────────────────────────────────

    /// <summary>現在のエンティティのラベル名を取り出す。</summary>
    public GraphTraversal<string> Label()
    {
        var lookup = new LabelNameLookupOp(_plan, _entityColumn);
        int labelCol = lookup.PredictedOutputColumnCount - 1;
        return Chain(lookup, row => row.GetString(labelCol), _entityColumn);
    }

    // ── ID 射影 ───────────────────────────────────────────────

    /// <summary>現在のエンティティ ID を <see cref="long"/> として取り出す。</summary>
    public GraphTraversal<long> Id()
    {
        var col = _entityColumn;
        return Chain(_plan, row => row.GetInt64(col), _entityColumn);
    }

    // ── エッジ端点解決 ──────────────────────────────────────

    /// <summary>現在のエッジのソース (起点) Vertexに解決する。</summary>
    public GraphTraversal<VertexId> SourceVertex()
    {
        // EdgeEndpointOp は単一 VertexId のタプルを放出し、上流を破棄する。
        // そのため生きていたエイリアスはすべて失われる。暗黙にドロップする既知制限。
        var rep = new EdgeEndpointOp(_plan, _entityColumn, EdgeEndpoint.Source);
        return Rebase<VertexId>(rep, row => row.GetVertexId(0), 0);
    }

    /// <summary>現在のエッジのターゲット (終点) Vertexに解決する。</summary>
    public GraphTraversal<VertexId> TargetVertex()
    {
        var rep = new EdgeEndpointOp(_plan, _entityColumn, EdgeEndpoint.Target);
        return Rebase<VertexId>(rep, row => row.GetVertexId(0), 0);
    }

    /// <summary>進入方向に対する「向こう側」の端点に解決する。</summary>
    public GraphTraversal<VertexId> OtherVertex()
    {
        var rep = new EdgeEndpointOp(_plan, _entityColumn, EdgeEndpoint.Other);
        return Rebase<VertexId>(rep, row => row.GetVertexId(0), 0);
    }

    /// <summary>
    /// graph-first KNN。上流の各Vertexを candidate set として KnnSearchFiltered を呼ぶ。
    /// 通常は <c>g.Knn(...).HasLabel(...).Has(...)</c> チェーンが LogicalOptimizer の KnnPushdown で
    /// 自動的にこの形に変換される。本メソッドは明示的に graph-first を選びたい (例: 二段 KNN や
    /// 複雑な candidate を作る場合) のエスケープハッチとして残す。詳細セマンティクスはクラスドキュメント参照。
    /// </summary>
    public GraphTraversal<VertexId> FilterByKnn(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
    {
        // Candidate を上流 plan に固定した graph-first KnnOp。Candidate != null のため
        // LogicalOptimizer は押し下げ判定をスキップし、そのまま FilteredKnn に物理化される。
        var filtered = new KnnOp(_plan, indexName, query.ToArray(), k, Dim: 0, options);
        return Rebase<VertexId>(filtered, row => row.GetVertexId(0), 0);
    }

    /// <summary>
    /// graph-first dyadic scoring。上流の候補Vertexからベクトルを gather し、
    /// ユーザー定義演算子でスコアリングして上位 k 件を放出する。
    /// </summary>
    internal GraphTraversal<VertexId> ApplyDyadicInternal(ApplyDyadicOp op)
        => Rebase<VertexId>(op, static row => row.GetVertexId(0), 0);

    /// <summary>
    /// graph-first 全文検索 (<c>.FilterByKnn</c> の BM25 版)。上流の各Vertexを candidate set として
    /// その中だけで BM25 top-k を求める。通常は <c>g.Search(...).HasLabel(...).Has(...)</c> チェーンが
    /// LogicalOptimizer の FullTextPushdown で自動的にこの形へ倒れるため、本メソッドは明示的に
    /// graph-first を選びたいときのエスケープハッチ。df / idf は全 postings から取るので候補ドキュメントの
    /// スコアは text-first と一致し、top-k だけが候補限定後に切られる (post-filter の k starvation を回避)。
    /// </summary>
    /// <param name="indexName">対象の全文索引名。</param>
    /// <param name="queryText">検索クエリ文字列。</param>
    /// <param name="k">取得する上位件数。</param>
    public GraphTraversal<VertexId> FilterByText(string indexName, string queryText, int k)
    {
        // Candidate を上流 plan に固定した graph-first FullTextScanOp。Candidate != null のため
        // optimizer は押し下げ判定をスキップし、そのまま FilteredFullTextScan に物理化される。
        var filtered = new FullTextScanOp(_plan, indexName, queryText, k, _stats?.FullTextCorpus(indexName));
        return Rebase<VertexId>(filtered, row => row.GetVertexId(0), 0);
    }

    // ── 並び替え ───────────────────────────────────────

    /// <summary>プロパティ <paramref name="key"/> の昇順でソートする。</summary>
    public GraphTraversal<T> OrderBy(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Chain(new SortOp(_plan, key, _plan.PredictedOutputColumnCount, Descending: false), _projection, _entityColumn);
    }

    /// <summary>プロパティ <paramref name="key"/> の降順でソートする。</summary>
    public GraphTraversal<T> OrderByDescending(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Chain(new SortOp(_plan, key, _plan.PredictedOutputColumnCount, Descending: true), _projection, _entityColumn);
    }

    /// <summary>現在のエンティティ ID 列でソートする。<paramref name="descending"/> が true なら降順。</summary>
    public GraphTraversal<T> Order(bool descending = false)
    {
        return Chain(new SortOp(_plan, null, _entityColumn, descending), _projection, _entityColumn);
    }

    // ── 数値集約 (終端、プロパティキーを引数に取る) ───────────

    /// <summary>プロパティ <paramref name="key"/> の <see cref="double"/> 合計を返す。空集合では 0 を返す。</summary>
    public double Sum(string key)
    {
        // full-scan + 数値列なら列スキャンで集計 (row path と同値、桁違いに高速)。
        if (TryFullScanColumnAggregate(key, out var agg) && IsNumericColumn(agg.ValueType))
            return agg.Count == 0 ? 0.0 : agg.Sum;
        return AggregateNumeric(key, AggregateKind.Sum) ?? 0.0;
    }

    /// <summary>プロパティ <paramref name="key"/> の <see cref="long"/> 合計を返す。空集合では 0 を返す。</summary>
    public long SumLong(string key)
    {
        // 整数列のみ列スキャン (row path の SumLong も Int64 スロットのみ合計するため同値)。
        if (TryFullScanColumnAggregate(key, out var agg) && IsIntegralColumn(agg.ValueType))
            return agg.LongSum;
        return (long)(AggregateLongSum(key) ?? 0L);
    }

    /// <summary>プロパティ <paramref name="key"/> の最大値を返す。要素が無い場合は <c>null</c>。</summary>
    public double? Max(string key)
    {
        if (TryFullScanColumnAggregate(key, out var agg) && IsNumericColumn(agg.ValueType))
            return agg.Count == 0 ? null : agg.Max;
        return AggregateNumeric(key, AggregateKind.Max);
    }

    /// <summary>プロパティ <paramref name="key"/> の最小値を返す。要素が無い場合は <c>null</c>。</summary>
    public double? Min(string key)
    {
        if (TryFullScanColumnAggregate(key, out var agg) && IsNumericColumn(agg.ValueType))
            return agg.Count == 0 ? null : agg.Min;
        return AggregateNumeric(key, AggregateKind.Min);
    }

    /// <summary>プロパティ <paramref name="key"/> の平均値を返す。要素が無い場合は <c>null</c>。</summary>
    public double? Mean(string key)
    {
        if (TryFullScanColumnAggregate(key, out var agg) && IsNumericColumn(agg.ValueType))
            return agg.Count == 0 ? null : agg.Sum / agg.Count;
        double sum = 0; long count = 0;
        ForEachNumeric(key, v => { sum += v; count++; });
        return count == 0 ? null : sum / count;
    }

    // row path が数値集約する型 (Int32/Int64 は Int64 スロット, Double) と合わせる。
    private static bool IsNumericColumn(Storage.Records.PropertyValueType t)
        => t is Storage.Records.PropertyValueType.Int32
             or Storage.Records.PropertyValueType.Int64
             or Storage.Records.PropertyValueType.Double;

    private static bool IsIntegralColumn(Storage.Records.PropertyValueType t)
        => t is Storage.Records.PropertyValueType.Int32
             or Storage.Records.PropertyValueType.Int64;

    /// <summary>
    /// チェーンが「ある kind の全件 full scan」なら、対象 <paramref name="key"/> が
    /// 列化済みかを backend に問い合わせ、列スキャンの集約結果を得る。full scan でない / 列が無い /
    /// mixed のときは false で、呼び出し側が row path にフォールバックする。
    /// </summary>
    private bool TryFullScanColumnAggregate(string key, out Quiver.ColumnAggregate agg)
    {
        agg = default;
        if (!TryDetectFullScanKind(out var kind)) return false;
        return _tx.AsInternal().TryColumnAggregate(kind, key, out agg);
    }

    /// <summary>チェーン起点が無フィルタの全件スキャン (全Vertex / 全リレーション) かを判定する。</summary>
    private bool TryDetectFullScanKind(out Core.EntityKind kind)
    {
        if (_plan is ScanOp { Kind: EntityKind.Vertex, Label: null }) { kind = Core.EntityKind.Vertex; return true; }
        if (_plan is ScanOp { Kind: EntityKind.Edge }) { kind = Core.EntityKind.Edge; return true; }
        kind = default;
        return false;
    }

    /// <summary>row path のプロパティ参照対象を現在の ID 型から決定する。</summary>
    private Core.EntityKind RowLookupKind()
        => typeof(T) == typeof(EdgeId) ? Core.EntityKind.Edge :
           typeof(T) == typeof(NexusId) ? Core.EntityKind.Nexus :
           Core.EntityKind.Vertex;

    private enum AggregateKind { Sum, Max, Min }

    private double? AggregateNumeric(string key, AggregateKind kind)
    {
        double acc = 0; bool seen = false;
        ForEachNumeric(key, v =>
        {
            if (!seen) { acc = v; seen = true; return; }
            acc = kind switch
            {
                AggregateKind.Sum => acc + v,
                AggregateKind.Max => v > acc ? v : acc,
                AggregateKind.Min => v < acc ? v : acc,
                _ => acc,
            };
        });
        return seen ? acc : null;
    }

    private long? AggregateLongSum(string key)
    {
        long acc = 0; bool seen = false;
        var plan = CompilePlan(new PropertyLookupOp(_plan, key, RowLookupKind()));
        using var cursor = _tx.ExecuteCursor(plan);
        int valueCol = cursor.Schema.Columns.Count - 1;
        while (cursor.MoveNext())
        {
            var row = cursor.Current;
            if (row.GetSlotType(valueCol) != TupleSlotType.Int64) continue;
            acc += row.GetInt64(valueCol);
            seen = true;
        }
        return seen ? acc : null;
    }

    private void ForEachNumeric(string key, Action<double> sink)
    {
        var plan = CompilePlan(new PropertyLookupOp(_plan, key, RowLookupKind()));
        using var cursor = _tx.ExecuteCursor(plan);
        int valueCol = cursor.Schema.Columns.Count - 1;
        while (cursor.MoveNext())
        {
            var row = cursor.Current;
            switch (row.GetSlotType(valueCol))
            {
                case TupleSlotType.Int64:  sink(row.GetInt64(valueCol)); break;
                case TupleSlotType.Double: sink(row.GetDouble(valueCol)); break;
            }
        }
    }

    // ── グルーピング ───────────────────────────────────────

    /// <summary>
    /// プロパティ <paramref name="key"/> の値ごとに件数を集計して辞書で返す。
    /// 文字列プロパティのみ対応。
    /// </summary>
    public Dictionary<string, long> GroupCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dict = new Dictionary<string, long>(StringComparer.Ordinal);
        var plan = CompilePlan(new PropertyLookupOp(_plan, key, EntityKind.Vertex));
        using var cursor = _tx.ExecuteCursor(plan);
        int valueCol = cursor.Schema.Columns.Count - 1;
        while (cursor.MoveNext())
        {
            var row = cursor.Current;
            if (row.GetSlotType(valueCol) != TupleSlotType.Utf8String) continue;
            var s = row.GetString(valueCol);
            dict[s] = dict.GetValueOrDefault(s) + 1;
        }
        return dict;
    }

    // ── fold ───────────────────────────────────────────

    /// <summary>結果を <see cref="List{T}"/> に畳み込む。<see cref="ToList"/> のエイリアス。</summary>
    public List<T> Fold() => ToList();

    // ── 重複排除 ──────────────────────────────────────

    /// <summary>現在のエンティティ列に対して重複排除を行う。</summary>
    public GraphTraversal<T> Dedup()
    {
        return Chain(new DedupOp(_plan, _entityColumn), _projection, _entityColumn);
    }

    // ── 可変長 repeat ─────────────────────────────────

    /// <summary>
    /// 指定回数だけ展開ステップを繰り返す可変長トラバーサル。
    /// <paramref name="emit"/> が true の場合は各ホップ後に中間結果も放出する。
    /// </summary>
    /// <param name="step">繰り返すステップを記述するアクション (例: <c>s => s.Out("KNOWS")</c>)。</param>
    /// <param name="times">繰り返し回数 (1 以上)。</param>
    /// <param name="emit">中間ホップを放出するかどうか。</param>
    public GraphTraversal<VertexId> Repeat(Action<RepeatStep> step, int times, bool emit = false)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (times < 1) throw new ArgumentOutOfRangeException(nameof(times), "Repeat には times >= 1 が必要です。");
        var rs = new RepeatStep();
        step(rs);
        int minHops = emit ? 1 : times;
        var b = new VarLenExpandOp(_plan, rs.Direction, rs.TypeFilter, minHops, times);
        int endCol = b.CurrentEntityColumn;
        return Rebase<VertexId>(b, row => row.GetVertexId(endCol), endCol);
    }

    // ── 最短経路 ─────────────────────────────────────

    /// <summary>
    /// 現在のVertexから <paramref name="target"/> までの最短ホップ数を返す。
    /// 到達不能な要素は放出しない。
    /// </summary>
    /// <param name="target">終点Vertex。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="type">辿るEdge型 (null なら全型)。</param>
    /// <param name="maxDistance">探索の上限ホップ数。</param>
    public GraphTraversal<long> ShortestPathTo(
        VertexId target,
        Direction direction = Direction.Outgoing,
        string? type = null,
        long maxDistance = long.MaxValue)
    {
        var b = new PathOp(_plan, target, direction, type, maxDistance);
        int distCol = b.CurrentEntityColumn;
        return Rebase<long>(b, row => row.GetInt64(distCol), distCol);
    }

    // ── union / coalesce / optional ──────────────────

    /// <summary>複数の分岐をすべて連結して放出する。</summary>
    public GraphTraversal<VertexId> Union(params Func<SubTraversal, SubTraversal>[] branches)
        => BuildBranched(branches, LogicalBranchKind.Union);

    /// <summary>左から順に評価し、最初にマッチした分岐の結果だけを放出する。</summary>
    public GraphTraversal<VertexId> Coalesce(params Func<SubTraversal, SubTraversal>[] branches)
        => BuildBranched(branches, LogicalBranchKind.Coalesce);

    /// <summary>
    /// 分岐がマッチすれば結果を放出し、マッチしなければ元のVertexをそのまま通す
    /// (Cypher の <c>OPTIONAL MATCH</c>)。
    /// </summary>
    public GraphTraversal<VertexId> Optional(Func<SubTraversal, SubTraversal> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return BuildBranched(new[] { branch }, LogicalBranchKind.Optional);
    }

    private GraphTraversal<VertexId> BuildBranched(Func<SubTraversal, SubTraversal>[] branches, LogicalBranchKind kind)
    {
        if (branches is null || branches.Length == 0)
            throw new ArgumentException("少なくとも 1 つの分岐が必要です。", nameof(branches));
        if (kind == LogicalBranchKind.Optional && branches.Length != 1)
            throw new ArgumentException("Optional は分岐を 1 つだけ受け取ります。", nameof(branches));

        var captured = branches;
        var b = new BranchOp(_plan, schema =>
        {
            var probes = new CorrelatedInputOperator[captured.Length];
            var ops = new IPhysicalOperator[captured.Length];
            for (int i = 0; i < captured.Length; i++)
            {
                var probe = new CorrelatedInputOperator();
                var seed = new CorrelatedInputOp(probe);
                var start = new SubTraversal(probe, seed, schema, 0);
                var leaf = captured[i](start);
                probes[i] = probe;
                ops[i] = leaf.BuildBranchOperator();
            }
            return (probes, ops);
        }, kind);
        return Rebase<VertexId>(b, row => row.GetVertexId(0), 0);
    }

    /// <summary>プロパティ <paramref name="key"/> の文字列値だけを取り出す。</summary>
    public GraphTraversal<string> Values(string key)
    {
        var lookup = new PropertyLookupOp(_plan, key, RowLookupKind());
        int propCol = lookup.PredictedOutputColumnCount - 1;
        return Chain(lookup, row => row.GetString(propCol), _entityColumn);
    }

    /// <summary>プロパティ <paramref name="key"/> の <see cref="float"/>[] 値を取り出す。</summary>
    internal GraphTraversal<float[]> ValuesFloatArray(string key)
    {
        var lookup = new PropertyLookupOp(_plan, key, EntityKind.Vertex);
        int propCol = lookup.PredictedOutputColumnCount - 1;
        return Chain(lookup, row =>
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(row.GetBytes(propCol).AsSpan()).ToArray(),
            _entityColumn);
    }

    // ── as / select — タプルスキーマ拡張 ──────────────────────────

    /// <summary>
    /// 現在のエンティティ列を <paramref name="label"/> にピン留めし、
    /// 下流の <see cref="Select(string)"/> から復元できるようにする。
    /// 後続の <c>Out</c>/<c>In</c>/<c>Both</c>/<c>OutEdges</c>/<c>InEdges</c>/<c>BothEdges</c>
    /// は pin した列を持ち越す (operator の末尾タプルスロットに保持) ため、
    /// メモリはエイリアス数 × 出力行数に比例して増える。
    /// </summary>
    /// <remarks>
    /// 制限: Repeat / ShortestPathTo / Union / Coalesce / Optional /
    /// SourceVertex/TargetVertex/OtherVertex / FilterByKnn はタプル形状を作り直すため、
    /// エイリアスは暗黙にドロップされる。必要なら下流で <c>.As</c> を再バインドすること。
    /// </remarks>
    public GraphTraversal<T> As(string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        var next = _aliases is null
            ? new Dictionary<string, int>(capacity: 1)
            : new Dictionary<string, int>(_aliases);
        next[label] = _entityColumn;

        var nextKinds = _aliasEntityKinds is null
            ? new Dictionary<string, EntityKind>(capacity: 1)
            : new Dictionary<string, EntityKind>(_aliasEntityKinds);
        if (TryGetEntityKind<T>(out var entityKind))
            nextKinds[label] = entityKind;
        else
            nextKinds.Remove(label);

        return new GraphTraversal<T>(
            _tx, _schema, _plan, _projection, _entityColumn, next, _stats,
            _hiddenNexusOriginColumn, nextKinds);
    }

    /// <summary>
    /// 以前 <see cref="As"/> で pin した列からトラバーサルを続行する。
    /// pin 先は常にエンティティ列のため、戻り値は
    /// <typeparamref name="T"/> によらず <see cref="VertexId"/> となる。以後のステップを通常通り連結できる。
    /// </summary>
    public GraphTraversal<VertexId> Select(string label)
        => Select<VertexId>(label);

    /// <summary>
    /// 以前 <see cref="As"/> で pin したエンティティ列へ戻り、
    /// 指定した ID 型のトラバーサルとして続行する。
    /// </summary>
    /// <typeparam name="TEntity">
    /// <see cref="VertexId"/>、<see cref="EdgeId"/>、<see cref="NexusId"/> のいずれか。
    /// </typeparam>
    /// <param name="label">復元する alias。</param>
    /// <exception cref="InvalidOperationException">
    /// alias が未定義、または alias のエンティティ種別と <typeparamref name="TEntity"/> が一致しない場合。
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TEntity"/> がサポート対象の ID 型ではない場合。
    /// </exception>
    public GraphTraversal<TEntity> Select<TEntity>(string label)
        where TEntity : struct
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        if (_aliases is null || !_aliases.TryGetValue(label, out var col))
            throw new InvalidOperationException($"エイリアス '{label}' は未定義です。先に .As(\"{label}\") で pin してください。");

        if (!TryGetEntityKind<TEntity>(out var requestedKind))
        {
            throw new NotSupportedException(
                $"Select<TEntity>(alias) は {nameof(VertexId)}、{nameof(EdgeId)}、"
                + $"{nameof(NexusId)} のみをサポートします。");
        }
        if (_aliasEntityKinds is null
            || !_aliasEntityKinds.TryGetValue(label, out var actualKind)
            || actualKind != requestedKind)
        {
            throw new InvalidOperationException(
                $"エイリアス '{label}' は {typeof(TEntity).Name} を保持していません。");
        }

        // plan / schema は変更しない。射影とエンティティ列を pin スロットに
        // 向け直すだけ。エイリアスは生きたままなので連鎖 .Select もそのまま機能する。
        return new GraphTraversal<TEntity>(
            _tx, _schema, _plan, row => ReadEntity<TEntity>(row, col), col,
            _aliases, _stats, aliasEntityKinds: _aliasEntityKinds);
    }

    private static bool TryGetEntityKind<TEntity>(out EntityKind kind)
    {
        if (typeof(TEntity) == typeof(VertexId))
        {
            kind = EntityKind.Vertex;
            return true;
        }
        if (typeof(TEntity) == typeof(EdgeId))
        {
            kind = EntityKind.Edge;
            return true;
        }
        if (typeof(TEntity) == typeof(NexusId))
        {
            kind = EntityKind.Nexus;
            return true;
        }

        kind = default;
        return false;
    }

    private static TEntity ReadEntity<TEntity>(QueryRow row, int column)
        where TEntity : struct
    {
        if (typeof(TEntity) == typeof(VertexId))
        {
            VertexId value = row.GetVertexId(column);
            return System.Runtime.CompilerServices.Unsafe.As<VertexId, TEntity>(ref value);
        }
        if (typeof(TEntity) == typeof(EdgeId))
        {
            EdgeId value = row.GetEdgeId(column);
            return System.Runtime.CompilerServices.Unsafe.As<EdgeId, TEntity>(ref value);
        }

        NexusId nexus = row.GetNexusId(column);
        return System.Runtime.CompilerServices.Unsafe.As<NexusId, TEntity>(ref nexus);
    }

    /// <summary>
    /// 各行をタプルとして返す終端射影。
    /// 射影クロージャは <see cref="MatchTuple"/> を受け取り、生のタプル列番号を
    /// 露出せずにエイリアス名で値を解決できる。
    /// </summary>
    /// <example>
    /// <code>
    /// var pairs = g.Vertices().As("a").Out("KNOWS").As("b")
    ///    .Select(t => (t.Vertex("a"), t.Vertex("b")));
    /// </code>
    /// </example>
    public List<TResult> Select<TResult>(Func<MatchTuple, TResult> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (_aliases is null || _aliases.Count == 0)
            throw new InvalidOperationException("Select(projection) はチェーン中に少なくとも 1 つの .As(label) が必要です。");
        var aliases = _aliases;
        var results = new List<TResult>();
        var qr = _tx.Execute(Compile());
        foreach (var row in qr.Rows())
            results.Add(projection(new MatchTuple(row, aliases)));
        return results;
    }

    /// <summary>すべての結果を <see cref="List{T}"/> に展開して返す。</summary>
    public List<T> ToList()
    {
        var results = new List<T>();
        var result  = _tx.Execute(Compile());
        foreach (var row in result.Rows())
            results.Add(_projection(row));
        return results;
    }

    /// <summary>
    /// 現在のVertexが指定条件で参加するNexusへ展開する。
    /// </summary>
    /// <param name="type">Nexus型。null は全型。</param>
    /// <param name="role">現在のVertexが担うロール。null は全ロール。</param>
    /// <returns>参加Nexus ID のトラバーサル。</returns>
    /// <remarks>
    /// Vertexごとの incidence チェーンを走査するため、計算量は対象Vertexの参加数に比例する。
    /// 展開元Vertexは <see cref="OtherMembers"/> が除外に使う内部文脈として保持される。
    /// </remarks>
    public GraphTraversal<NexusId> Nexuses(string? type = null, string? role = null)
    {
        int[]? carry = null;
        Dictionary<string, int>? aliases = null;
        if (_aliases is not null)
        {
            (carry, aliases) = RemapForExpand(baseColumnCount: 2);
        }

        var expand = new ExpandToNexusOp(_plan, _entityColumn, type, role, carry);
        return new GraphTraversal<NexusId>(
            _tx, _schema, expand, row => row.GetNexusId(1), 1,
            aliases, _stats, hiddenNexusOriginColumn: 0,
            aliasEntityKinds: aliases is null ? null : _aliasEntityKinds);
    }

    /// <summary>現在のNexusを構成するメンバーVertexへ展開する。</summary>
    /// <param name="role">返すメンバーのロール。null は全ロール。</param>
    /// <returns>メンバーVertex ID のトラバーサル。</returns>
    /// <remarks>
    /// Nexusごとの incidence チェーンを走査するため、計算量はアリティに比例する。
    /// 通常の全メンバー展開なので、以前の vertex → nexus 展開元は結果に含まれ得る。
    /// </remarks>
    public GraphTraversal<VertexId> Members(string? role = null)
        => ExpandNexusMembers(role, excludeOrigin: false);

    /// <summary>
    /// 現在のNexusのメンバーから、このNexusへ到達した起点Vertexを除いて返す。
    /// </summary>
    /// <param name="role">返すメンバーのロール。null は全ロール。</param>
    /// <returns>起点以外のメンバーVertex ID のトラバーサル。</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>g.Nexuses()</c> や <c>g.Nexus(id)</c> のように、展開元Vertexを持たない起点から呼び出した場合。
    /// </exception>
    /// <remarks>
    /// <c>g.Vertex(person).Nexuses("Meeting").OtherMembers("attendee")</c> のような
    /// co-membership 走査に使う。起点Vertexが複数ロールで参加していても、そのVertex ID は全ロールから除外する。
    /// </remarks>
    public GraphTraversal<VertexId> OtherMembers(string? role = null)
    {
        if (!_hiddenNexusOriginColumn.HasValue)
        {
            throw new InvalidOperationException(
                "OtherMembers() は vertex traversal の Nexuses() に続けて使用してください。"
                + " Nexus起点から全メンバーを取得する場合は Members() を使用してください。");
        }
        return ExpandNexusMembers(role, excludeOrigin: true);
    }

    private GraphTraversal<VertexId> ExpandNexusMembers(string? role, bool excludeOrigin)
    {
        int[]? carry = null;
        Dictionary<string, int>? aliases = null;
        if (_aliases is not null)
        {
            (carry, aliases) = RemapForExpand(baseColumnCount: 2);
        }

        var expand = new ExpandMembersOp(
            _plan,
            _entityColumn,
            role,
            excludeOrigin ? _hiddenNexusOriginColumn : null,
            carry);
        // Members の結果は (nexus, member) に形を作り直す。vertex -> nexus の
        // hidden origin はここで意図的に破棄し、次の Nexuses が新しい起点を設定する。
        return new GraphTraversal<VertexId>(
            _tx, _schema, expand, row => row.GetVertexId(1), 1, aliases, _stats,
            aliasEntityKinds: aliases is null ? null : _aliasEntityKinds);
    }

    /// <summary>最初の 1 件を返す。結果が空のときは <see cref="InvalidOperationException"/> を投げる。</summary>
    public T Next()
    {
        var result = _tx.Execute(Compile());
        foreach (var row in result.Rows())
            return _projection(row);
        throw new InvalidOperationException("トラバーサルが結果を生成しませんでした。");
    }

    /// <summary>最初の 1 件を返す。結果が空のときは <see langword="default"/> を返す。</summary>
    public T? TryNext()
    {
        var result = _tx.Execute(Compile());
        foreach (var row in result.Rows())
            return _projection(row);
        return default;
    }

    /// <summary>結果の件数だけを数える終端ステップ。</summary>
    public long Count()
    {
        long count = 0;
        var result = _tx.Execute(Compile());
        foreach (var _ in result.Rows())
            count++;
        return count;
    }

    /// <summary>
    /// 結果のストリーミングカーソルを返す。カーソルの寿命は呼び出し側が管理し、
    /// 必ず <see cref="IDisposable.Dispose"/> を呼ぶこと。所属トランザクションが
    /// 生きている間だけ有効。
    /// </summary>
    public ITraversalCursor<T> AsCursor()
    {
        var cursor = _tx.ExecuteCursor(Compile());
        return new TraversalCursor<T>(cursor, _projection);
    }

    /// <summary>
    /// 結果を逐次列挙する <see cref="IEnumerable{T}"/> を返す。全件を一度に
    /// メモリに乗せず、所属トランザクションが生きている間だけ有効。
    /// </summary>
    public IEnumerable<T> AsEnumerable()
    {
        using var cursor = _tx.ExecuteCursor(Compile());
        while (cursor.MoveNext())
            yield return _projection(cursor.Current);
    }

}

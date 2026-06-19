using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// <see cref="GraphTraversalSource"/> から派生するグラフトラバーサルチェーン
/// </summary>
/// <typeparam name="T">現在のチェーンが放出する要素の型</typeparam>
/// <remarks>
/// <see cref="ToList"/> / <see cref="Next"/> / <see cref="AsCursor"/> などで閉じる。
/// </remarks>
// <summary>
// Gremlin 風のグラフトラバーサルチェーン。
// <see cref="GraphTraversalSource"/> から派生し、<c>HasLabel</c> / <c>Has</c> /
// <c>Out</c> / <c>OutRelationships</c> / <c>Where</c> / <c>Limit</c> / <c>OrderBy</c>
// などのステップをチェーンして最終的に <see cref="ToList"/> / <see cref="Next"/> /
// <see cref="AsCursor"/> などの終端で実行する。
// </summary>
// <typeparam name="T">現在のチェーンが放出する要素の型 (典型的には <see cref="NodeId"/> や <see cref="RelationshipId"/>)。</typeparam>
// <remarks>
// インスタンスは不変。チェーンの各ステップは新しい <see cref="GraphTraversal{T}"/> を返すため、
// 中間結果を変数に保持して分岐させても副作用は発生しない。所属トランザクションの境界を
// 越えて利用しないこと。
// <para>
// 各ステップは <see cref="LogicalOp"/> (論理プラン IR) を構築するだけで、KNN 押し下げ等の
// 最適化は終端で <see cref="LogicalOptimizer"/> に委譲する (旧 <c>PendingKnnBuilder</c> の DSL 埋め込みは撤去)。
// </para>
// </remarks>
public sealed class GraphTraversal<T>
{
    internal readonly IGraphTransaction _tx;
    internal readonly ISchemaApi _schema;
    internal readonly LogicalOp _plan;
    internal readonly Func<QueryRow, T> _projection;
    internal readonly int _entityColumn;
    // GC-6: エイリアス名 → 列インデックスのマップ。チェーン上で一度もエイリアスが
    // バインドされていない (一般的なケース) 場合は null。不変として扱い、丸ごと
    // 差し替える運用。インプレース変更は行わない。
    internal readonly Dictionary<string, int>? _aliases;
    // VEC-10: 任意で注入された GraphStats。LogicalOptimizer.Optimize に渡し、
    // graph-first push-down を label cardinality 30% 以上のときに vector-first にフォールバックさせる。
    // 既存呼び出しは null のまま (= VEC-9 動作 = 構造ヒントのみで判定)。
    internal readonly GraphStats? _stats;

    // GC-8: この列が保持するエンティティ種別。T が RelationshipId なら
    // エッジトラバーサル (OutRelationships<T>() 等) なので述語をリレーションシップ
    // プロパティ読みに切り替える。それ以外 (NodeId / string 等) はノード。
    private static readonly PredicateEntity EntityKindForT =
        typeof(T) == typeof(RelationshipId) ? PredicateEntity.Relationship : PredicateEntity.Node;

    internal GraphTraversal(
        IGraphTransaction tx,
        ISchemaApi schema,
        LogicalOp plan,
        Func<QueryRow, T> projection,
        int entityColumn,
        Dictionary<string, int>? aliases = null,
        GraphStats? stats = null)
    {
        _tx = tx; _schema = schema; _plan = plan; _projection = projection;
        _entityColumn = entityColumn;
        _aliases = (aliases is { Count: > 0 }) ? aliases : null;
        _stats = stats;
    }

    /// <summary>同じエイリアスセットを引き継いだ後続トラバーサルを構築する内部ヘルパ。</summary>
    private GraphTraversal<U> Chain<U>(LogicalOp plan, Func<QueryRow, U> projection, int entityColumn)
        => new(_tx, _schema, plan, projection, entityColumn, _aliases, _stats);

    /// <summary>alias を持ち越さない (= タプル形状をリセットする) 新規 traversal を構築する内部ヘルパ。stats だけは引き継ぐ。</summary>
    private GraphTraversal<U> Rebase<U>(LogicalOp plan, Func<QueryRow, U> projection, int entityColumn, Dictionary<string, int>? aliases = null)
        => new(_tx, _schema, plan, projection, entityColumn, aliases, _stats);

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
    // Gremlin の <c>.hasLabel</c>)相当のステップ。
    // 起点が <c>AllNodesScan</c> の場合は <c>NodeByLabelScan</c> に置き換える最適化を行い、
    // それ以外はラベル述語のフィルタとして連結する。
    public GraphTraversal<NodeId> HasLabel(string label)
    {
        LogicalOp next;
        if (_plan is ScanOp { Kind: EntityKind.Node })
            next = new ScanOp(EntityKind.Node, _schema.GetOrCreateLabel(label));
        else
        {
            var labelId = _schema.GetOrCreateLabel(label);
            var col = _entityColumn;
            next = new FilterOp(_plan, _ => new LabelPredicate(labelId, col));
        }
        return new GraphTraversal<NodeId>(_tx, _schema, next, row => row.GetNodeId(_entityColumn), next.CurrentEntityColumn, _aliases, _stats);
    }

    /// <summary>
    /// pure-filter を plan に積む共通ヘルパ。<paramref name="factoryWithCol"/> は filter を適用する
    /// 対象列番号を受け取り、predicate ファクトリを返す。KNN 押し下げ (candidate-side rewrite) は
    /// 終端で <see cref="LogicalOptimizer"/> が <see cref="FilterOp"/> 連鎖から再構成する。
    /// </summary>
    private GraphTraversal<T> ApplyPureFilter(Func<int, Func<ISchemaApi, IPredicate>> factoryWithCol)
        => Chain(new FilterOp(_plan, factoryWithCol(_entityColumn)), _projection, _entityColumn);

    /// <summary>プロパティ <paramref name="key"/> が文字列 <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, string value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        return ApplyPureFilter(col => _ => new PropertyEqStringPredicate(col, keyId, value) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="int"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, int value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var pred  = P.Eq((long)value);
        return ApplyPureFilter(col => _ => new PropertyInt64Predicate(col, keyId, pred) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="long"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, long value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var pred  = P.Eq(value);
        return ApplyPureFilter(col => _ => new PropertyInt64Predicate(col, keyId, pred) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="double"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, double value)
    {
        var keyId   = _schema.GetOrCreatePropertyKey(key);
        var encoded = BitConverter.DoubleToInt64Bits(value);
        return ApplyPureFilter(col => _ => new PropertyDoublePredicate(col, keyId, encoded) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="bool"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public GraphTraversal<T> Has(string key, bool value)
    {
        var keyId  = _schema.GetOrCreatePropertyKey(key);
        var scalar = value ? 1L : 0L;
        return ApplyPureFilter(col => _ => new PropertyBoolPredicate(col, keyId, scalar) { Entity = EntityKindForT });
    }

    /// <summary>
    /// プロパティ <paramref name="key"/> が <see cref="PropertyPredicate"/> に適応する要素のみを通す。
    /// </summary>
    // (Gremlin の <c>.has("key", P.gt(10))</c> 相当)。
    public GraphTraversal<T> Has(string key, PropertyPredicate pred)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        return ApplyPureFilter(col => _ => PredicateDispatch.Build(col, keyId, pred, EntityKindForT));
    }

    /// <summary>外向きにリレーションシップを辿り、接続された隣接ノードを返す。</summary>
    // (Gremlin の <c>.out</c>)
    public GraphTraversal<NodeId> Out(string? type = null) => Expand(Direction.Outgoing, type);

    /// <summary>外向きにリレーションシップを辿り、接続された隣接ノードを返す。</summary>
    public GraphTraversal<NodeId> Out<TRel>() where TRel : IGraphRelationship<TRel> => Out(TRel.GraphType);

    /// <summary>内向きにリレーションシップを辿り、接続された隣接ノードを返す。</summary>
    // (Gremlin の <c>.in</c>)
    public GraphTraversal<NodeId> In(string? type = null) => Expand(Direction.Incoming, type);

    /// <summary>内向きにリレーションシップを辿り、接続された隣接ノードを返す。</summary>
    public GraphTraversal<NodeId> In<TRel>() where TRel : IGraphRelationship<TRel> => In(TRel.GraphType);

    /// <summary>向きを指定せずリレーションシップを辿り、接続された隣接ノードを返す。</summary>
    // (Gremlin の <c>.both</c>)
    public GraphTraversal<NodeId> Both(string? type = null) => Expand(Direction.Both, type);

    /// <summary>向きを指定せずリレーションシップを辿り、接続された隣接ノードを返す。</summary>
    public GraphTraversal<NodeId> Both<TRel>() where TRel : IGraphRelationship<TRel> => Both(TRel.GraphType);

    private GraphTraversal<NodeId> Expand(Direction direction, string? type)
    {
        // GC-6: エイリアスが生きているときは展開を通してそれらをコピーし、
        // 下流の .Select(alias) が元のエンティティを引けるようにする。
        // エイリアスが無ければ GC-6 以前と同じ fast path (余分列なし) と等価。
        // _entityColumn を明示渡しすることで、.Select(alias).Out(...) が
        // 直近 plan の出力ではなく pin された列から展開できる。
        if (_aliases is null)
        {
            var fast = new ExpandOp(_plan, _entityColumn, direction, type, ExpandOutputMode.NeighborOnly, null);
            return Rebase<NodeId>(fast, row => row.GetNodeId(fast.CurrentEntityColumn), fast.CurrentEntityColumn);
        }

        var (carry, newAliases) = RemapForExpand(baseColumnCount: 1);
        var expand = new ExpandOp(_plan, _entityColumn, direction, type, ExpandOutputMode.NeighborOnly, carry);
        return Rebase<NodeId>(expand, row => row.GetNodeId(0), 0, newAliases);
    }

    /// <summary>外向きリレーションシップを返す。</summary>
    // (Gremlin の <c>.outE</c>、Quiver 改名後の名称)
    public GraphTraversal<RelationshipId> OutRelationships(string? type = null) => ExpandRelationship(Direction.Outgoing, type);

    /// <summary>外向きリレーションシップを返す。</summary>
    public GraphTraversal<RelationshipId> OutRelationships<TRel>() where TRel : IGraphRelationship<TRel> => OutRelationships(TRel.GraphType);

    /// <summary>内向きリレーションシップを返す。</summary>
    // (Gremlin の <c>.inE</c>、Quiver 改名後の名称)
    public GraphTraversal<RelationshipId> InRelationships(string? type = null) => ExpandRelationship(Direction.Incoming, type);

    /// <summary>内向きリレーションシップを返す。</summary>
    public GraphTraversal<RelationshipId> InRelationships<TRel>() where TRel : IGraphRelationship<TRel> => InRelationships(TRel.GraphType);

    /// <summary>双方向リレーションシップを返す。</summary>
    // (Gremlin の <c>.bothE</c>、Quiver 改名後の名称)
    public GraphTraversal<RelationshipId> BothRelationships(string? type = null) => ExpandRelationship(Direction.Both, type);

    /// <summary>双方向リレーションシップを返す。</summary>
    public GraphTraversal<RelationshipId> BothRelationships<TRel>() where TRel : IGraphRelationship<TRel> => BothRelationships(TRel.GraphType);

    private GraphTraversal<RelationshipId> ExpandRelationship(Direction direction, string? type)
    {
        if (_aliases is null)
        {
            var fast = new ExpandOp(_plan, _entityColumn, direction, type, ExpandOutputMode.NeighborAndRel, null);
            return Rebase<RelationshipId>(fast, row => row.GetRelationshipId(0), 0);
        }

        // NeighborAndRel は 2 列 (rel@0, neighbor@1) を放出する。連鎖する
        // .SourceNode() / .TargetNode() のための「カレント」列は 0 (rel) のままなので、
        // carry は 2 から始まる。
        var (carry, newAliases) = RemapForExpand(baseColumnCount: 2);
        var e = new ExpandOp(_plan, _entityColumn, direction, type, ExpandOutputMode.NeighborAndRel, carry);
        return Rebase<RelationshipId>(e, row => row.GetRelationshipId(0), 0, newAliases);
    }

    /// <summary>
    /// Out/In/Both と OutRelationships/InRelationships/BothRelationships の共通ヘルパ。
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
    // 例: <c>.Where(t =&gt; t.Out("KNOWS"))</c> — KNOWS エッジを持つノードのみを通す。
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
    /// <summary>
    // (Cypher の <c>WHERE NOT EXISTS{...}</c> 相当)
    // 例: <c>.Not(t =&gt; t.Out("KNOWS"))</c> — KNOWS エッジを持たないノードのみを通す。
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

    // ── GC-1: presence checks ────────────────────────────────────────────────

    /// <summary>プロパティ <paramref name="key"/> を保持する要素のみを通す。</summary>
    // (Gremlin の <c>.has(key)</c>)。
    public GraphTraversal<T> Has(string key)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        return ApplyPureFilter(col => _ => new PropertyExistsPredicate(col, keyId, mustExist: true) { Entity = EntityKindForT });
    }

    /// <summary>プロパティ <paramref name="key"/> を持たない要素のみを通す。</summary>
    // (Gremlin の <c>.hasNot</c>)
    public GraphTraversal<T> HasNot(string key)
    {
        // TryGet があれば不要なトークン ID 割り当てを避けられるが、ISchemaApi.TryGet は
        // 現状公開されていない。最悪ケースで初回呼び出し時にトークン 1 個を割り当てるだけで
        // 動作は正しい — 新規作成されたキーには観測値が 0 件のため。
        var keyId = _schema.GetOrCreatePropertyKey(key);
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

    // ── GC-2: traversal-level boolean composition ────────────────────────────

    /// <summary>
    /// すべてのサブトラバーサルが少なくとも 1行を生成する要素のみを通す。
    /// </summary>
    // Gremlin の <c>.and(t1, t2, …)</c> — 
    public GraphTraversal<T> And(params Func<SubTraversal, SubTraversal>[] traversals)
    {
        if (traversals is null || traversals.Length == 0)
            throw new ArgumentException("And には少なくとも 1 つのサブトラバーサルが必要です。", nameof(traversals));
        return CombineSubTraversals(traversals, useOr: false);
    }

    /// <summary>いずれかのサブトラバーサルがマッチする要素のみを通す。</summary>
    // Gremlin の <c>.or(t1, t2, …)</c> — 
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

    // ── GC-1: pagination ─────────────────────────────────────────────────────

    /// <summary>最大 <paramref name="n"/> 件まで放出する</summary>
    // (Gremlin の <c>.limit</c>)。
    public GraphTraversal<T> Limit(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        // VEC-9: KNN 直上の Limit は LogicalOptimizer の KnnLimitPushdown が K を min(K,n) に縮める。
        return Chain(new LimitOp(_plan, n, Skip: 0), _projection, _entityColumn);
    }

    /// <summary>先頭 <paramref name="n"/> 件をスキップしてから放出を開始する。</summary>
    // (Gremlin の <c>.skip</c>)。
    public GraphTraversal<T> Skip(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        return Chain(new LimitOp(_plan, long.MaxValue, Skip: n), _projection, _entityColumn);
    }

    /// <summary>半開区間 <c>[from, to)</c> のウィンドウを放出する。</summary>
    // (Gremlin の <c>.range(a, b)</c>)
    public GraphTraversal<T> Range(long from, long to)
    {
        if (from < 0 || to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "0 <= from <= to を満たす必要があります。");
        return Chain(new LimitOp(_plan, to - from, Skip: from), _projection, _entityColumn);
    }

    // ── GC-1: 終端 / 存在判定 ──────────────────────────────────────

    /// <summary>トラバーサルが少なくとも 1件の要素が存在するか。</summary>
    // (Gremlin の <c>.hasNext</c>)
    public bool HasNext()
    {
        using var cursor = AsCursor();
        return cursor.MoveNext();
    }

    // ── GC-1: <c>.label()</c> ステップ ──────────────────────────────

    /// <summary>現在のエンティティのラベル名を取り出す。</summary>
    // (Gremlin の <c>.label()</c>)
    public GraphTraversal<string> Label()
    {
        var lookup = new LabelNameLookupOp(_plan, _entityColumn);
        int labelCol = lookup.PredictedOutputColumnCount - 1;
        return Chain(lookup, row => row.GetString(labelCol), _entityColumn);
    }

    // ── GC-1: <c>.id()</c> ステップ ─────────────────────────────────

    /// <summary>現在のエンティティ ID を <see cref="long"/> として取り出す。</summary>
    // (Gremlin の <c>.id()</c>)
    public GraphTraversal<long> Id()
    {
        var col = _entityColumn;
        return Chain(_plan, row => row.GetInt64(col), _entityColumn);
    }

    // ── GC-1: エッジ端点解決 ──────────────────────────────────────

    /// <summary>現在のエッジのソース (起点) ノードに解決する。</summary>
    // Gremlin の <c>.outV()</c> — 
    public GraphTraversal<NodeId> SourceNode()
    {
        // RelationshipEndpointOp は単一 NodeId のタプルを放出し、上流を破棄する。
        // そのため生きていたエイリアスはすべて失われる。GC-6 Phase 2 の既知制限として
        // 暗黙にドロップする運用。
        var rep = new RelationshipEndpointOp(_plan, _entityColumn, RelationshipEndpoint.Source);
        return Rebase<NodeId>(rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>現在のエッジのターゲット (終点) ノードに解決する。</summary>
    // Gremlin の <c>.inV()</c> — 
    public GraphTraversal<NodeId> TargetNode()
    {
        var rep = new RelationshipEndpointOp(_plan, _entityColumn, RelationshipEndpoint.Target);
        return Rebase<NodeId>(rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>進入方向に対する「向こう側」の端点に解決する。</summary>
    // Gremlin の <c>.otherV()</c> — 
    public GraphTraversal<NodeId> OtherNode()
    {
        var rep = new RelationshipEndpointOp(_plan, _entityColumn, RelationshipEndpoint.Other);
        return Rebase<NodeId>(rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>
    /// graph-first KNN。上流の各ノードを candidate set として KnnSearchFiltered を呼ぶ。
    /// 通常は <c>g.Knn(...).HasLabel(...).Has(...)</c> チェーンが LogicalOptimizer の KnnPushdown で
    /// 自動的にこの形に変換される。本メソッドは明示的に graph-first を選びたい (例: 二段 KNN や
    /// 複雑な candidate を作る場合) のエスケープハッチとして残す。詳細セマンティクスはクラスドキュメント参照。
    /// </summary>
    public GraphTraversal<NodeId> FilterByKnn(string indexName, ReadOnlySpan<float> query, int k)
    {
        // Candidate を上流 plan に固定した graph-first KnnOp。Candidate != null のため
        // LogicalOptimizer は押し下げ判定をスキップし、そのまま FilteredKnn に物理化される。
        var filtered = new KnnOp(_plan, indexName, query.ToArray(), k, Dim: 0);
        return Rebase<NodeId>(filtered, row => row.GetNodeId(0), 0);
    }

    /// <summary>
    /// graph-first dyadic scoring。上流の候補ノードからベクトルを gather し、
    /// ユーザー定義演算子でスコアリングして上位 k 件を放出する。
    /// </summary>
    internal GraphTraversal<NodeId> ApplyDyadicInternal(ApplyDyadicOp op)
        => Rebase<NodeId>(op, static row => row.GetNodeId(0), 0);

    /// <summary>
    /// graph-first 全文検索 (<c>.FilterByKnn</c> の BM25 版)。上流の各ノードを candidate set として
    /// その中だけで BM25 top-k を求める。通常は <c>g.Search(...).HasLabel(...).Has(...)</c> チェーンが
    /// LogicalOptimizer の FullTextPushdown で自動的にこの形へ倒れるため、本メソッドは明示的に
    /// graph-first を選びたいときのエスケープハッチ。df / idf は全 postings から取るので候補ドキュメントの
    /// スコアは text-first と一致し、top-k だけが候補限定後に切られる (post-filter の k starvation を回避)。
    /// </summary>
    /// <param name="indexName">対象の全文索引名。</param>
    /// <param name="queryText">検索クエリ文字列。</param>
    /// <param name="k">取得する上位件数。</param>
    public GraphTraversal<NodeId> FilterByText(string indexName, string queryText, int k)
    {
        // Candidate を上流 plan に固定した graph-first FullTextScanOp。Candidate != null のため
        // optimizer は押し下げ判定をスキップし、そのまま FilteredFullTextScan に物理化される。
        var filtered = new FullTextScanOp(_plan, indexName, queryText, k, _stats?.FullTextCorpus(indexName));
        return Rebase<NodeId>(filtered, row => row.GetNodeId(0), 0);
    }

    // ── GC-3: 並び替え ───────────────────────────────────────

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

    // ── GC-3: 数値集約 (終端、プロパティキーを引数に取る) ───────────

    /// <summary>プロパティ <paramref name="key"/> の <see cref="double"/> 合計を返す。空集合では 0 を返す。</summary>
    public double Sum(string key)
    {
        // ARCH-5c Phase 5d: full-scan + 数値列なら列スキャンで集計 (row path と同値、桁違いに高速)。
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

    // ARCH-5c Phase 5d: row path が数値集約する型 (Int32/Int64 は Int64 スロット, Double) と合わせる。
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

    /// <summary>チェーン起点が無フィルタの全件スキャン (全ノード / 全リレーション) かを判定する。</summary>
    private bool TryDetectFullScanKind(out Core.EntityKind kind)
    {
        if (_plan is ScanOp { Kind: EntityKind.Node, Label: null }) { kind = Core.EntityKind.Node; return true; }
        if (_plan is ScanOp { Kind: EntityKind.Relationship }) { kind = Core.EntityKind.Relationship; return true; }
        kind = default;
        return false;
    }

    /// <summary>row path のプロパティ参照対象が rel か node か (g.Relationships() 起点なら rel)。</summary>
    private Core.EntityKind RowLookupKind()
        => _plan is ScanOp { Kind: EntityKind.Relationship } ? Core.EntityKind.Relationship : Core.EntityKind.Node;

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

    // ── GC-3: グルーピング ───────────────────────────────────────

    /// <summary>
    /// プロパティ <paramref name="key"/> の値ごとに件数を集計して辞書で返す
    /// (Gremlin の <c>.groupCount(key)</c>)。文字列プロパティのみ対応。
    /// </summary>
    public Dictionary<string, long> GroupCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dict = new Dictionary<string, long>(StringComparer.Ordinal);
        var plan = CompilePlan(new PropertyLookupOp(_plan, key, EntityKind.Node));
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

    // ── GC-3: fold ───────────────────────────────────────────

    /// <summary>結果を <see cref="List{T}"/> に畳み込む (Gremlin の <c>.fold()</c>)。<see cref="ToList"/> のエイリアス。</summary>
    public List<T> Fold() => ToList();

    // ── GC-4: 重複排除 ──────────────────────────────────────

    /// <summary>現在のエンティティ列に対して重複排除を行う (Gremlin の <c>.dedup()</c>)。</summary>
    public GraphTraversal<T> Dedup()
    {
        return Chain(new DedupOp(_plan, _entityColumn), _projection, _entityColumn);
    }

    // ── GC-4: 可変長 repeat ─────────────────────────────────

    /// <summary>
    /// 指定回数だけ展開ステップを繰り返す可変長トラバーサル (Gremlin の <c>.repeat(...).times(n)</c>)。
    /// <paramref name="emit"/> が true の場合は各ホップ後に中間結果も放出する。
    /// </summary>
    /// <param name="step">繰り返すステップを記述するアクション (例: <c>s => s.Out("KNOWS")</c>)。</param>
    /// <param name="times">繰り返し回数 (1 以上)。</param>
    /// <param name="emit">中間ホップを放出するかどうか。</param>
    public GraphTraversal<NodeId> Repeat(Action<RepeatStep> step, int times, bool emit = false)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (times < 1) throw new ArgumentOutOfRangeException(nameof(times), "Repeat には times >= 1 が必要です。");
        var rs = new RepeatStep();
        step(rs);
        int minHops = emit ? 1 : times;
        var b = new VarLenExpandOp(_plan, rs.Direction, rs.TypeFilter, minHops, times);
        int endCol = b.CurrentEntityColumn;
        return Rebase<NodeId>(b, row => row.GetNodeId(endCol), endCol);
    }

    // ── GC-4: 最短経路 ─────────────────────────────────────

    /// <summary>
    /// 現在のノードから <paramref name="target"/> までの最短ホップ数を返す
    /// (Gremlin の <c>.shortestPath()</c>)。到達不能な要素は放出しない。
    /// </summary>
    /// <param name="target">終点ノード。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="type">辿るリレーションシップ型 (null なら全型)。</param>
    /// <param name="maxDistance">探索の上限ホップ数。</param>
    public GraphTraversal<long> ShortestPathTo(
        NodeId target,
        Direction direction = Direction.Outgoing,
        string? type = null,
        long maxDistance = long.MaxValue)
    {
        var b = new PathOp(_plan, target, direction, type, maxDistance);
        int distCol = b.CurrentEntityColumn;
        return Rebase<long>(b, row => row.GetInt64(distCol), distCol);
    }

    // ── GC-4: union / coalesce / optional ──────────────────

    /// <summary>複数の分岐をすべて連結して放出する (Gremlin の <c>.union(...)</c>)。</summary>
    public GraphTraversal<NodeId> Union(params Func<SubTraversal, SubTraversal>[] branches)
        => BuildBranched(branches, LogicalBranchKind.Union);

    /// <summary>左から順に評価し、最初にマッチした分岐の結果だけを放出する (Gremlin の <c>.coalesce(...)</c>)。</summary>
    public GraphTraversal<NodeId> Coalesce(params Func<SubTraversal, SubTraversal>[] branches)
        => BuildBranched(branches, LogicalBranchKind.Coalesce);

    /// <summary>
    /// 分岐がマッチすれば結果を放出し、マッチしなければ元のノードをそのまま通す
    /// (Cypher の <c>OPTIONAL MATCH</c>)。
    /// </summary>
    public GraphTraversal<NodeId> Optional(Func<SubTraversal, SubTraversal> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return BuildBranched(new[] { branch }, LogicalBranchKind.Optional);
    }

    private GraphTraversal<NodeId> BuildBranched(Func<SubTraversal, SubTraversal>[] branches, LogicalBranchKind kind)
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
        return Rebase<NodeId>(b, row => row.GetNodeId(0), 0);
    }

    /// <summary>プロパティ <paramref name="key"/> の文字列値だけを取り出す (Gremlin の <c>.values(key)</c>)。</summary>
    public GraphTraversal<string> Values(string key)
    {
        var lookup = new PropertyLookupOp(_plan, key, EntityKind.Node);
        int propCol = lookup.PredictedOutputColumnCount - 1;
        return Chain(lookup, row => row.GetString(propCol), _entityColumn);
    }

    /// <summary>プロパティ <paramref name="key"/> の <see cref="float"/>[] 値を取り出す。</summary>
    internal GraphTraversal<float[]> ValuesFloatArray(string key)
    {
        var lookup = new PropertyLookupOp(_plan, key, EntityKind.Node);
        int propCol = lookup.PredictedOutputColumnCount - 1;
        return Chain(lookup, row =>
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(row.GetBytes(propCol).AsSpan()).ToArray(),
            _entityColumn);
    }

    // ── GC-6: as / select — タプルスキーマ拡張 ──────────────────────────

    /// <summary>
    /// Gremlin の <c>.as("label")</c> — 現在のエンティティ列を <paramref name="label"/> に
    /// ピン留めし、下流の <see cref="Select(string)"/> から復元できるようにする。
    /// 後続の <c>Out</c>/<c>In</c>/<c>Both</c>/<c>OutRelationships</c>/<c>InRelationships</c>/<c>BothRelationships</c>
    /// は pin した列を持ち越す (operator の末尾タプルスロットに保持) ため、
    /// メモリはエイリアス数 × 出力行数に比例して増える。
    /// </summary>
    /// <remarks>
    /// 制限: Repeat / ShortestPathTo / Union / Coalesce / Optional /
    /// SourceNode/TargetNode/OtherNode / FilterByKnn はタプル形状を作り直すため、
    /// エイリアスは暗黙にドロップされる。必要なら下流で <c>.As</c> を再バインドすること。
    /// </remarks>
    public GraphTraversal<T> As(string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        var next = _aliases is null
            ? new Dictionary<string, int>(capacity: 1)
            : new Dictionary<string, int>(_aliases);
        next[label] = _entityColumn;
        return new GraphTraversal<T>(_tx, _schema, _plan, _projection, _entityColumn, next, _stats);
    }

    /// <summary>
    /// Gremlin の <c>.select("label")</c> — 以前 <see cref="As"/> で pin した列から
    /// トラバーサルを続行する。pin 先は常にエンティティ列のため、戻り値は
    /// <typeparamref name="T"/> によらず <see cref="NodeId"/> となる。以後のステップを通常通り連結できる。
    /// </summary>
    public GraphTraversal<NodeId> Select(string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        if (_aliases is null || !_aliases.TryGetValue(label, out var col))
            throw new InvalidOperationException($"エイリアス '{label}' は未定義です。先に .As(\"{label}\") で pin してください。");
        // plan / schema は変更しない。射影とエンティティ列を pin スロットに
        // 向け直すだけ。エイリアスは生きたままなので連鎖 .Select もそのまま機能する。
        return new GraphTraversal<NodeId>(_tx, _schema, _plan, row => row.GetNodeId(col), col, _aliases, _stats);
    }

    /// <summary>
    /// Gremlin の <c>.select("a","b",…)</c> — 各行をタプルとして返す終端射影。
    /// 射影クロージャは <see cref="MatchTuple"/> を受け取り、生のタプル列番号を
    /// 露出せずにエイリアス名で値を解決できる。
    /// </summary>
    /// <example>
    /// <code>
    /// var pairs = g.Nodes().As("a").Out("KNOWS").As("b")
    ///     .Select(t => (t.Node("a"), t.Node("b")));
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

    /// <summary>結果の件数だけを数える終端ステップ (Gremlin の <c>.count()</c>)。</summary>
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

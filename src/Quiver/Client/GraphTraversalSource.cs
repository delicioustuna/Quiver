using System.Collections.Immutable;
using Quiver.Api.Internal;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// Gremlin 風のグラフトラバーサルを構築するエントリポイント。
/// <see cref="GraphTransactionExtensions.G"/> 拡張で取得し、ノード追加・
/// リレーション追加・スキャン起点・Match DSL・KNN 検索の起点として用いる。
/// </summary>
/// <remarks>
/// 同一トランザクション中で複数のトラバーサルを並行して生成できるが、
/// インスタンス自体はスレッドセーフではない。トランザクション境界を越えて
/// 共有しないこと。
/// </remarks>
public sealed class GraphTraversalSource
{
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;
    // VEC-10: 任意で注入された GraphStats。PendingKnnBuilder.Materialize 経由で
    // graph-first push-down を label cardinality 30% 以上で vector-first フォールバックさせる。
    // null のときは VEC-9 動作 (構造ヒントのみで判定)。
    private readonly GraphStats? _stats;

    /// <summary>
    /// 指定したトランザクションとスキーマでトラバーサルソースを生成する。
    /// 通常は <see cref="GraphTransactionExtensions.G"/> 経由で呼び出す。
    /// </summary>
    /// <param name="tx">所属するグラフトランザクション。</param>
    /// <param name="schema">ラベル / プロパティキー / リレーションシップ型を解決するスキーマ API。</param>
    public GraphTraversalSource(IGraphTransaction tx, ISchemaApi schema)
        : this(tx, schema, stats: null)
    {
    }

    /// <summary>
    /// GraphStats を注入してトラバーサルソースを生成する。
    /// 後段 <c>g.Knn(...).HasLabel(L)</c> 形式の push-down リライト時に、
    /// label cardinality が <see cref="Internal.PendingKnnBuilder.VectorFirstLabelFraction"/>
    /// (既定 30%) 以上のときに vector-first フォールバックを選ぶための判定材料となる。
    /// stats を渡さない場合は構造ヒントのみで graph-first を選ぶ。
    /// </summary>
    public GraphTraversalSource(IGraphTransaction tx, ISchemaApi schema, GraphStats? stats)
    {
        _tx = tx; _schema = schema; _stats = stats;
    }

    // ── ノード書き込み ──────────────────────────────────────────────────────

    /// <summary>
    /// 新しいノードビルダを開始する。
    /// <c>g.AddNode("Person").P("Name", "Alice").Next()</c> のように呼ぶ。
    /// </summary>
    /// <param name="label">作成するノードのラベル名。</param>
    public NodeBuilder         AddNode(string label) => new(_tx, label);

    /// <summary>
    /// 新しいリレーションシップビルダを開始する。
    /// <c>g.AddRelationship("KNOWS").From(a).To(b).Next()</c> のように呼ぶ。
    /// </summary>
    /// <param name="type">作成するリレーションシップの型名。</param>
    public RelationshipBuilder AddRelationship(string type) => new(_tx, type);

    /// <summary>
    /// Cypher の <c>MERGE (n:label {matchKey: matchValue})</c> に相当する糖衣構文。
    /// <see cref="IGraphTransaction.MergeNode"/> のラッパで、<c>Created</c> フラグを
    /// 用いて ON CREATE SET / ON MATCH SET の分岐を呼び出し側で書ける。
    /// </summary>
    /// <param name="label">マージ対象ノードのラベル。</param>
    /// <param name="matchKey">マッチに用いるプロパティキー。</param>
    /// <param name="matchValue">マッチに用いるプロパティ値。</param>
    /// <returns>マッチした or 作成されたノード ID と、新規作成だったかを表すフラグの組。</returns>
    public (NodeId Id, bool Created) MergeNode(string label, string matchKey, in Storage.Records.PropertyValue matchValue)
        => _tx.MergeNode(label, matchKey, in matchValue);

    // ── エンティティ操作糖衣 (IGraphNode<T> ベース) ─────────────────────────

    /// <summary><see cref="IGraphNode{T}"/> 実装型を用いた型安全な Insert。</summary>
    public NodeId Insert<T>(T entity)             where T : IGraphNode<T> => T.Insert(_tx, entity);

    /// <summary><see cref="IGraphNode{T}"/> 実装型を用いた Insert + インデックス登録。</summary>
    public NodeId InsertIndexed<T>(T entity)      where T : IGraphNode<T> => T.InsertIndexed(_tx, entity);

    /// <summary>指定 ID のノードプロパティを <typeparamref name="T"/> インスタンスに復元する。</summary>
    public T      Load<T>(NodeId id)              where T : IGraphNode<T> => T.Load(_tx, id);

    /// <summary>指定 ID のノードのプロパティを <paramref name="entity"/> の値で上書きする。</summary>
    public void   Update<T>(NodeId id, T entity)  where T : IGraphNode<T> => T.Update(_tx, id, entity);

    /// <summary>指定 ID のノードを削除する。</summary>
    public void   Delete<T>(NodeId id)            where T : IGraphNode<T> => T.Delete(_tx, id);

    // ── スキャン起点 ─────────────────────────────────────────────────────────

    /// <summary>全ノードをスキャン起点とするトラバーサルを生成する (Gremlin の <c>g.V()</c> 相当)。</summary>
    public GraphTraversal<NodeId> Nodes()
    {
        var plan = new ScanOp(EntityKind.Node, null);
        return new GraphTraversal<NodeId>(_tx, _schema, plan, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>
    /// 全リレーションシップをスキャン起点とするトラバーサル (Gremlin の <c>g.E()</c> 相当)。
    /// 主用途は全件集約 (<c>Sum</c>/<c>Mean</c>/<c>Max</c>/<c>Min</c>) で、対象プロパティが列化済みなら
    /// 列スキャンで高速集計する (それ以外は row path フォールバック)。<c>ToList()</c> で全 rel ID も取れる。
    /// </summary>
    public GraphTraversal<RelationshipId> Relationships()
    {
        var plan = new ScanOp(EntityKind.Relationship, null);
        return new GraphTraversal<RelationshipId>(_tx, _schema, plan, row => row.GetRelationshipId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>指定 ID のノード 1 件だけを起点とするトラバーサル (Gremlin の <c>g.V(id)</c> 相当)。</summary>
    public GraphTraversal<NodeId> Node(NodeId nodeId)
    {
        var plan = new NodeSeedOp(new[] { nodeId });
        return new GraphTraversal<NodeId>(_tx, _schema, plan, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>指定 ID のノード群を起点とするトラバーサル (Gremlin の <c>g.V(ids)</c> 相当)。</summary>
    public GraphTraversal<NodeId> Nodes(params NodeId[] nodeIds)
    {
        var plan = new NodeSeedOp(nodeIds);
        return new GraphTraversal<NodeId>(_tx, _schema, plan, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    // ── 型付きスキャン起点 ────────────────────────────────────────────────────

    /// <summary>
    /// <typeparamref name="T"/> の <see cref="IGraphNode{T}.GraphLabel"/> でフィルタした
    /// 型付きトラバーサルを生成する。<c>Has(p => p.Name, "Alice")</c> のような
    /// 式ツリーベースのプロパティ参照が利用可能になる。
    /// </summary>
    public TypedGraphTraversal<T> Nodes<T>() where T : IGraphNode<T>
    {
        var inner = Nodes().HasLabel(T.GraphLabel);
        return new TypedGraphTraversal<T>(inner, _tx, _schema);
    }

    // ── Match DSL ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Match DSL クエリを開始する。<see cref="GraphPattern"/> でパターンを構築し、
    /// <c>Where</c> / <c>Return</c> をチェーンして結果を取得する。
    /// </summary>
    /// <param name="pattern">マッチするノード / エッジパターン。</param>
    public MatchQuery Match(GraphPattern pattern) => new(_tx, _schema, pattern);

    // ── VEC-5: KNN スキャン起点 ────────────────────────────────────────────────

    /// <summary>
    /// ベクトル類似度上位 k 件をスキャン起点とするトラバーサル。
    /// 類似度の降順でノード ID を放出し、<c>.HasLabel(...)</c> や <c>.Out(...)</c> を
    /// 続けて KNN とグラフトラバーサルを組み合わせられる (codex_advice_3.md 6.4 節)。
    /// </summary>
    /// <remarks>
    /// 類似度スコア自体は伝播しない。生スコアが必要な場合は
    /// <c>db.Vectors.KnnSearch(...)</c> を直接呼び出すこと。
    /// インデックスは <see cref="Core.EntityKind.Node"/> にバインドされている必要がある。
    /// リレーションシップ向け KNN は具体的なユースケースが出るまで意図的にスコープ外とする。
    /// <para>
    /// <c>g.Knn(...).HasLabel(...).Has(...)</c> のような後続 pure-filter チェーンは
    /// 自動的に candidate-side に巻き戻され、<see cref="GraphTraversal{T}.FilterByKnn"/> 相当の
    /// graph-first プランに変換される。フィルタが小さい場合は数倍〜数十倍高速化される。
    /// 明示的な graph-first 制御が必要な場合のみ <see cref="GraphTraversal{T}.FilterByKnn"/> を直接呼ぶ。
    /// </para>
    /// <para>
    /// <c>g.Knn(idx, q, k).Limit(n)</c> で <c>n &lt; k</c> のとき、KNN の k を <c>min(k, n)</c> に
    /// 縮めて実行する (後段 filter は candidate-side 処理済のため安全)。
    /// </para>
    /// </remarks>
    /// <param name="indexName">対象のベクトルインデックス名。</param>
    /// <param name="query">問い合わせベクトル。</param>
    /// <param name="k">取得する上位件数。</param>
    public GraphTraversal<NodeId> Knn(string indexName, ReadOnlySpan<float> query, int k)
    {
        // VEC-9: PendingKnnBuilder で包み、後続の pure-filter / Limit を candidate-side に
        // 巻き戻せるようにする。filter が積まれなければ terminal で vector-first に materialize される。
        // VEC-10: _stats を引き継ぎ、Materialize 経路で label cardinality fallback を効かせる。
        // VEC-12: backend が capability 経路で spec を返せれば dim を解決し、PendingKnnBuilder に
        //         dim-aware piecewise threshold を引かせる。spec を返さない backend では dim=0 で
        //         legacy 30% 単一閾値経路に倒れる (HasFastLabelIndex 経路は使われない)。
        int dim = _tx.AsInternal().Access.TryGetVectorIndexSpec(indexName, out var spec) ? spec.Dimensions : 0;
        // ARCH-7: vector-first を既定とする KnnOp(Candidate=null) を積む。後続の pure-filter / Limit は
        // 終端で LogicalOptimizer の KnnPushdown が candidate-side に巻き戻して graph-first 化を判定する。
        var plan = new KnnOp(null, indexName, query.ToArray(), k, dim);
        return new GraphTraversal<NodeId>(_tx, _schema, plan, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>
    /// FTS-3: 全文索引に対する BM25 検索を起点にトラバーサルを開始する (<c>g.Knn</c> と対称)。
    /// クエリは索引構築時と同一のトークナイザ (catalog 記録の TokenizerId) で分割され、
    /// term-at-a-time BM25 で上位 <paramref name="k"/> 件を関連度降順に放出する。続けて
    /// <c>.Out(...)</c> 等のトラバーサルステップを接続できる。
    /// </summary>
    /// <remarks>
    /// 関連度スコア自体は伝播しない (KNN と同じ MVP 方針)。可視性は世代照合で
    /// フィルタされる (削除/再利用された slot を指す postings は除外)。
    /// graph-first 経路 (<c>.FilterByText</c> / pushdown) は FTS-4。
    /// </remarks>
    /// <param name="indexName">対象の全文索引名。</param>
    /// <param name="queryText">検索クエリ文字列。</param>
    /// <param name="k">取得する上位件数。</param>
    public GraphTraversal<NodeId> Search(string indexName, string queryText, int k)
    {
        var plan = new FullTextScanOp(null, indexName, queryText, k);
        return new GraphTraversal<NodeId>(_tx, _schema, plan, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>
    /// FTS-5: BM25 全文検索と KNN ベクトル検索の上位 <paramref name="k"/> 件を
    /// RRF (Reciprocal Rank Fusion) で融合したハイブリッド検索を起点にトラバーサルを開始する。
    /// 各検索が独立に上位 <paramref name="k"/> 件を関連度順に求め、両ランキングの順位
    /// (<c>Σ 1/(60 + rank)</c>) を合算して融合上位 <paramref name="k"/> 件を放出する。
    /// 続けて <c>.Out(...)</c> 等のトラバーサルステップを接続できる。
    /// </summary>
    /// <remarks>
    /// RRF は順位のみで計算でき score 配管を要さないため、BM25 / KNN いずれの leaf も
    /// 既存の「score 非公開」設計のまま融合できる (design 13 §7.2)。両方に上位で現れる
    /// 文書ほど押し上がり、片方にしか現れない文書もそのランクで残る。可視性は各 leaf 側で
    /// 既にフィルタ済み。weighted-sum 融合は非目標 (距離スケール調整が必要なため)。
    /// </remarks>
    /// <param name="textIndex">対象の全文索引名。</param>
    /// <param name="queryText">全文検索クエリ文字列。</param>
    /// <param name="vectorIndex">対象のベクトル索引名。</param>
    /// <param name="queryVector">問い合わせベクトル。</param>
    /// <param name="k">融合後に取得する上位件数。</param>
    public GraphTraversal<NodeId> HybridSearch(
        string textIndex, string queryText,
        string vectorIndex, ReadOnlySpan<float> queryVector, int k)
    {
        int dim = _tx.AsInternal().Access.TryGetVectorIndexSpec(vectorIndex, out var spec) ? spec.Dimensions : 0;
        var children = ImmutableArray.Create<LogicalOp>(
            new FullTextScanOp(null, textIndex, queryText, k),
            new KnnOp(null, vectorIndex, queryVector.ToArray(), k, dim));
        var plan = new FusionOp(children, k, FusionStrategy.Rrf);
        return new GraphTraversal<NodeId>(_tx, _schema, plan, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    // ── 重み付き最短経路 (Dijkstra / A*) ───────────────────────────────────────

    /// <summary>
    /// <paramref name="source"/> から <paramref name="target"/> までの
    /// <em>重み付き</em>最短経路を Dijkstra 法で求める。各エッジの重みは
    /// リレーションシッププロパティ <paramref name="weightKey"/> (数値型) から読む。
    /// プロパティを持たないエッジの重みは 1.0 として扱う。
    /// </summary>
    /// <remarks>
    /// ホップ数最短の <see cref="GraphTraversal{T}.ShortestPathTo"/> と異なり、
    /// 結果は重み合計が最小の経路を、距離 + ノード列 + エッジ列として返す。
    /// 重みは非負でなければならない (負の重みを検出すると
    /// <see cref="InvalidOperationException"/>)。
    /// </remarks>
    /// <param name="source">始点ノード。</param>
    /// <param name="target">終点ノード。</param>
    /// <param name="weightKey">エッジ重みを保持するリレーションシッププロパティのキー名。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="type">辿るリレーションシップ型 (null なら全型)。</param>
    /// <param name="maxDistance">この重み合計を超える経路は探索しない (既定: 無制限)。</param>
    public WeightedPathResult WeightedShortestPath(
        NodeId source, NodeId target, string weightKey,
        Direction direction = Direction.Outgoing,
        string? type = null,
        double maxDistance = double.PositiveInfinity)
        => RunWeightedShortestPath(source, target, weightKey, direction, type, maxDistance, heuristic: null);

    /// <summary>
    /// ユーザ提供のヒューリスティック <paramref name="heuristic"/> を用いた A* 探索で
    /// 重み付き最短経路を求める。<paramref name="heuristic"/> は各ノードから終点までの
    /// 推定残コストを返す。最適解を保証するには consistent (単調) かつ非負である必要がある。
    /// </summary>
    /// <param name="source">始点ノード。</param>
    /// <param name="target">終点ノード。</param>
    /// <param name="weightKey">エッジ重みを保持するリレーションシッププロパティのキー名。</param>
    /// <param name="heuristic">ノード → 終点までの推定残コスト。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="type">辿るリレーションシップ型 (null なら全型)。</param>
    /// <param name="maxDistance">この重み合計を超える経路は探索しない (既定: 無制限)。</param>
    public WeightedPathResult WeightedShortestPath(
        NodeId source, NodeId target, string weightKey,
        Func<NodeId, double> heuristic,
        Direction direction = Direction.Outgoing,
        string? type = null,
        double maxDistance = double.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(heuristic);
        return RunWeightedShortestPath(source, target, weightKey, direction, type, maxDistance, heuristic);
    }

    /// <summary>
    /// ノードの座標プロパティから自動生成したヒューリスティックを用いた A* 探索で
    /// 重み付き最短経路を求める。各ノードの座標は数値プロパティ
    /// <paramref name="xKey"/> / <paramref name="yKey"/> から読む。
    /// </summary>
    /// <remarks>
    /// <see cref="HeuristicMetric.Euclidean"/> では平面距離、
    /// <see cref="HeuristicMetric.Haversine"/> では緯度経度 (度) からの大圏距離 (m) を
    /// 推定残コストとする。最適解を保証するには、ヒューリスティックがエッジ重みの単位で
    /// 実経路長の下界になっている必要がある (例: 重みが平面距離なら Euclidean、
    /// メートルの道路距離なら Haversine)。
    /// </remarks>
    /// <param name="source">始点ノード。</param>
    /// <param name="target">終点ノード。</param>
    /// <param name="weightKey">エッジ重みを保持するリレーションシッププロパティのキー名。</param>
    /// <param name="xKey">X 座標 (経度) を保持するノードプロパティのキー名。</param>
    /// <param name="yKey">Y 座標 (緯度) を保持するノードプロパティのキー名。</param>
    /// <param name="metric">座標から推定残コストを計算する距離尺度。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="type">辿るリレーションシップ型 (null なら全型)。</param>
    /// <param name="maxDistance">この重み合計を超える経路は探索しない (既定: 無制限)。</param>
    public WeightedPathResult WeightedShortestPathAStar(
        NodeId source, NodeId target, string weightKey,
        string xKey, string yKey,
        HeuristicMetric metric = HeuristicMetric.Euclidean,
        Direction direction = Direction.Outgoing,
        string? type = null,
        double maxDistance = double.PositiveInfinity)
    {
        ArgumentException.ThrowIfNullOrEmpty(xKey);
        ArgumentException.ThrowIfNullOrEmpty(yKey);

        double targetX = NodeCoordinate(target, xKey);
        double targetY = NodeCoordinate(target, yKey);

        Func<NodeId, double> heuristic = metric == HeuristicMetric.Haversine
            ? node => Haversine(NodeCoordinate(node, yKey), NodeCoordinate(node, xKey), targetY, targetX)
            : node =>
            {
                double dx = NodeCoordinate(node, xKey) - targetX;
                double dy = NodeCoordinate(node, yKey) - targetY;
                return Math.Sqrt(dx * dx + dy * dy);
            };

        return RunWeightedShortestPath(source, target, weightKey, direction, type, maxDistance, heuristic);
    }

    private WeightedPathResult RunWeightedShortestPath(
        NodeId source, NodeId target, string weightKey,
        Direction direction, string? type, double maxDistance,
        Func<NodeId, double>? heuristic)
    {
        ArgumentException.ThrowIfNullOrEmpty(weightKey);

        var keyId = _schema.GetOrCreatePropertyKey(weightKey);
        var weightProvider = new PropertyChainWeightProvider(keyId);
        RelationshipTypeId? typeId = type != null ? _schema.GetOrCreateRelationshipType(type) : null;

        var pair = new PairWithConstantOperator(new SingleNodeOperator(source), 0, target);
        var op = new WeightedShortestPathOperator(
            pair, 0, 1, direction, typeId, weightProvider, heuristic, maxDistance);

        using var result = _tx.Execute(op);
        foreach (var row in result.Rows())
        {
            WeightedPathCodec.Decode(row.GetBytes(3), out var nodes, out var rels);
            return new WeightedPathResult(true, row.GetDouble(2), nodes, rels);
        }
        return WeightedPathResult.NotFound;
    }

    private double NodeCoordinate(NodeId node, string key)
    {
        var v = _tx.GetProperty(node, key);
        return v.Type switch
        {
            Storage.Records.PropertyValueType.Double => v.DoubleValue,
            Storage.Records.PropertyValueType.Int64 => v.Int64Value,
            Storage.Records.PropertyValueType.Int32 => v.Int32Value,
            _ => throw new InvalidOperationException(
                $"ノード {node.Value} の座標プロパティ '{key}' が数値型ではありません (型: {v.Type})。"),
        };
    }

    /// <summary>緯度経度 (度) 2 点間の大圏距離をメートルで返す。</summary>
    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6_371_000.0;
        const double degToRad = Math.PI / 180.0;
        double dLat = (lat2 - lat1) * degToRad;
        double dLon = (lon2 - lon1) * degToRad;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * degToRad) * Math.Cos(lat2 * degToRad)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadiusMeters * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }
}

/// <summary><see cref="IGraphTransaction"/> から <see cref="GraphTraversalSource"/> を取得する拡張メソッド。</summary>
public static class GraphTransactionExtensions
{
    /// <summary>
    /// トランザクションとスキーマから新規 <see cref="GraphTraversalSource"/> を構築する。
    /// </summary>
    public static GraphTraversalSource G(this IGraphTransaction tx, ISchemaApi schema)
        => new(tx, schema);

    /// <summary>
    /// GraphStats を渡してトラバーサルソースを構築する。
    /// <c>g.Knn(...).HasLabel(L)</c> 形式の push-down が、L の cardinality が高いときに
    /// vector-first にフォールバックして wall-clock 劣化を回避できる。stats を渡さない場合
    /// は構造ヒントのみで graph-first を選ぶ。
    /// </summary>
    public static GraphTraversalSource G(this IGraphTransaction tx, ISchemaApi schema, GraphStats? stats)
        => new(tx, schema, stats);
}

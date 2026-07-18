using System.Collections.Immutable;
using Quiver.Api.Internal;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Api;

/// <summary>
/// グラフトラバーサルを構築するエントリポイント。
/// <see cref="IReadTransaction.Query"/> から取得し、scan、Match DSL、
/// KNN 検索の起点として用いる。
/// </summary>
/// <remarks>
/// 同一トランザクション中で複数のトラバーサルを並行して生成できるが、
/// インスタンス自体はスレッドセーフではない。トランザクション境界を越えて
/// 共有しないこと。
/// </remarks>
public sealed class GraphTraversalSource
{
    private readonly IReadTransaction _tx;
    private readonly ISchemaCatalog _schema;
    // 任意で注入された GraphStats。KNN push-down 時に label cardinality が高ければ
    // vector-first フォールバックさせる。null のときは構造ヒントのみで判定する。
    private readonly GraphStats? _stats;

    /// <summary>
    /// 指定したトランザクションとスキーマでトラバーサルソースを生成する。
    /// 通常は <see cref="IReadTransaction.Query"/> 経由で取得する。
    /// </summary>
    /// <param name="tx">所属するグラフトランザクション。</param>
    /// <param name="schema">ラベル / プロパティキー / Edge型を解決するスキーマ API。</param>
    internal GraphTraversalSource(IReadTransaction tx, ISchemaCatalog schema)
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
    internal GraphTraversalSource(IReadTransaction tx, ISchemaCatalog schema, GraphStats? stats)
    {
        _tx = tx; _schema = schema; _stats = stats;
    }

    /// <summary>同じ transaction/schema に query planning 用統計を関連付けた source を返す。</summary>
    public GraphTraversalSource WithStats(GraphStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new GraphTraversalSource(_tx, _schema, stats);
    }

    /// <summary>指定 ID のVertexプロパティを <typeparamref name="T"/> インスタンスに復元する。</summary>
    public T      Load<T>(VertexId id)              where T : IGraphVertex<T> => T.Load(_tx, id);

    // ── スキャン起点 ─────────────────────────────────────────────────────────

    /// <summary>全Vertexをスキャン起点とするトラバーサルを生成する。</summary>
    public GraphTraversal<VertexId> Vertices()
    {
        var plan = new ScanOp(EntityKind.Vertex, null);
        return new GraphTraversal<VertexId>(_tx, _schema, plan, row => row.GetVertexId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>
    /// 全Edgeをスキャン起点とするトラバーサル。
    /// 主用途は全件集約 (<c>Sum</c>/<c>Mean</c>/<c>Max</c>/<c>Min</c>) で、対象プロパティが列化済みなら
    /// 列スキャンで高速集計する (それ以外は row path フォールバック)。<c>ToList()</c> で全 edge ID も取れる。
    /// </summary>
    public GraphTraversal<EdgeId> Edges()
    {
        var plan = new ScanOp(EntityKind.Edge, null);
        return new GraphTraversal<EdgeId>(_tx, _schema, plan, row => row.GetEdgeId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>
    /// 可視な全Nexusをスキャン起点とするトラバーサルを生成する。
    /// Nexusは、購入の buyer/item やファクトの subject/source のように、
    /// 役割の異なる複数Vertexを一つの関係として束ねるエンティティである。
    /// </summary>
    /// <remarks>全件走査は O(H)。後続の <c>Members(role)</c> でロール別に参加Vertexへ展開できる。</remarks>
    public GraphTraversal<NexusId> Nexuses()
    {
        var plan = new ScanOp(EntityKind.Nexus, null);
        return new GraphTraversal<NexusId>(
            _tx, _schema, plan, row => row.GetNexusId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>指定 ID のNexus 1 件を起点とするトラバーサルを生成する。</summary>
    /// <param name="nexusId">起点にするNexus ID。</param>
    /// <remarks>
    /// この起点には「どのVertexから到達したか」という文脈がないため、
    /// <c>OtherMembers()</c> ではなく <c>Members()</c> を使用する。
    /// </remarks>
    public GraphTraversal<NexusId> Nexus(NexusId nexusId)
    {
        var plan = new NexusSeedOp(nexusId);
        return new GraphTraversal<NexusId>(
            _tx, _schema, plan, row => row.GetNexusId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>指定 ID のVertex 1 件だけを起点とするトラバーサル。</summary>
    public GraphTraversal<VertexId> Vertex(VertexId vertexId)
    {
        var plan = new VertexSeedOp(new[] { vertexId });
        return new GraphTraversal<VertexId>(_tx, _schema, plan, row => row.GetVertexId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>指定 ID のVertex群を起点とするトラバーサル。</summary>
    public GraphTraversal<VertexId> Vertices(params VertexId[] vertexIds)
    {
        var plan = new VertexSeedOp(vertexIds);
        return new GraphTraversal<VertexId>(_tx, _schema, plan, row => row.GetVertexId(0), 0, aliases: null, stats: _stats);
    }

    // ── 型付きスキャン起点 ────────────────────────────────────────────────────

    /// <summary>
    /// <typeparamref name="T"/> の <see cref="IGraphVertex{T}.GraphLabel"/> でフィルタした
    /// 型付きトラバーサルを生成する。<c>Has(p => p.Name, "Alice")</c> のような
    /// 式ツリーベースのプロパティ参照が利用可能になる。
    /// </summary>
    public TypedGraphTraversal<T> Vertices<T>() where T : IGraphVertex<T>
    {
        var inner = Vertices().HasLabel(T.GraphLabel);
        return new TypedGraphTraversal<T>(inner, _tx, _schema);
    }

    // ── Match DSL ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Match DSL クエリを開始する。<see cref="GraphPattern"/> でパターンを構築し、
    /// <c>Where</c> / <c>Return</c> をチェーンして結果を取得する。
    /// </summary>
    /// <param name="pattern">マッチするVertex / エッジパターン。</param>
    public MatchQuery Match(GraphPattern pattern) => new(_tx, _schema, pattern);

    /// <summary>
    /// 星型Nexusパターンで Match DSL クエリを開始する。
    /// <see cref="GraphPattern.Nexus(string, string?)"/> と
    /// <see cref="NexusPattern.Member(string, VertexPattern)"/> で構築したパターンを渡すと、
    /// 一つのNexusと役割別メンバーが同じ行に束ねられる。
    /// </summary>
    /// <param name="pattern">マッチする星型Nexusパターン。</param>
    public MatchQuery Match(NexusPattern pattern) => new(_tx, _schema, pattern);

    // ── KNN スキャン起点 ────────────────────────────────────────────────

    /// <summary>
    /// ベクトル類似度上位 k 件をスキャン起点とするトラバーサル。
    /// 類似度の降順でVertex ID を放出し、<c>.HasLabel(...)</c> や <c>.Out(...)</c> を
    /// 続けて KNN とグラフトラバーサルを組み合わせられる。
    /// </summary>
    /// <remarks>
    /// 類似度スコア自体は伝播しない。生スコアが必要な場合は
    /// <c>db.Vectors.KnnSearch(...)</c> を直接呼び出すこと。
    /// インデックスは <see cref="Core.EntityKind.Vertex"/> にバインドされている必要がある。
    /// Edge向け KNN は具体的なユースケースが出るまで意図的にスコープ外とする。
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
    /// <param name="options">探索精度と探索量を制御する実行時オプション。null は既定値。</param>
    public GraphTraversal<VertexId> Knn(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
    {
        // 後続の pure-filter / Limit を candidate-side に巻き戻せるよう KnnOp で包む。
        // filter が積まれなければ終端で vector-first に materialize される。
        // _stats があれば label cardinality fallback を効かせる。
        // backend が spec を返せれば dim-aware piecewise threshold を使い、返さなければ
        // dim=0 で単一閾値経路にフォールバックする。
        int dim = _tx.AsInternal().Access.TryGetVectorIndexSpec(indexName, out var spec) ? spec.Dimensions : 0;
        // vector-first を既定とし、後続 pure-filter / Limit は終端で KnnPushdown が
        // candidate-side に巻き戻して graph-first 化を判定する。
        var plan = new KnnOp(null, indexName, query.ToArray(), k, dim, options);
        return new GraphTraversal<VertexId>(_tx, _schema, plan, row => row.GetVertexId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>
    /// 全文索引に対する BM25 検索を起点にトラバーサルを開始する (<c>g.Knn</c> と対称)。
    /// クエリは索引構築時と同一のトークナイザ (catalog 記録の TokenizerId) で分割され、
    /// term-at-a-time BM25 で上位 <paramref name="k"/> 件を関連度降順に放出する。続けて
    /// <c>.Out(...)</c> 等のトラバーサルステップを接続できる。
    /// </summary>
    /// <remarks>
    /// 関連度スコア自体は伝播しない (KNN と同じ MVP 方針)。可視性は世代照合で
    /// フィルタされる (削除/再利用された slot を指す postings は除外)。
    /// graph-first 経路は <c>.FilterByText</c> (pushdown) 側で扱う。
    /// </remarks>
    /// <param name="indexName">対象の全文索引名。</param>
    /// <param name="queryText">検索クエリ文字列。</param>
    /// <param name="k">取得する上位件数。</param>
    public GraphTraversal<VertexId> Search(string indexName, string queryText, int k)
    {
        // stats があれば N/avgdl スナップショットを op に焼き込み、クエリ毎の norms 走査を省く。
        var plan = new FullTextScanOp(null, indexName, queryText, k, _stats?.FullTextCorpus(indexName));
        return new GraphTraversal<VertexId>(_tx, _schema, plan, row => row.GetVertexId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>
    /// BM25 全文検索と KNN ベクトル検索の上位 <paramref name="k"/> 件を
    /// RRF (Reciprocal Rank Fusion) で融合したハイブリッド検索を起点にトラバーサルを開始する。
    /// 各検索が独立に上位 <paramref name="k"/> 件を関連度順に求め、両ランキングの順位
    /// (<c>Σ 1/(60 + rank)</c>) を合算して融合上位 <paramref name="k"/> 件を放出する。
    /// 続けて <c>.Out(...)</c> 等のトラバーサルステップを接続できる。
    /// </summary>
    /// <remarks>
    /// RRF は順位のみで計算でき score 配管を要さないため、BM25 / KNN いずれの leaf も
    /// 既存の「score 非公開」設計のまま融合できる。両方に上位で現れる
    /// 文書ほど押し上がり、片方にしか現れない文書もそのランクで残る。可視性は各 leaf 側で
    /// 既にフィルタ済み。weighted-sum 融合は非目標 (距離スケール調整が必要なため)。
    /// </remarks>
    /// <param name="textIndex">対象の全文索引名。</param>
    /// <param name="queryText">全文検索クエリ文字列。</param>
    /// <param name="vectorIndex">対象のベクトル索引名。</param>
    /// <param name="queryVector">問い合わせベクトル。</param>
    /// <param name="k">融合後に取得する上位件数。</param>
    public GraphTraversal<VertexId> HybridSearch(
        string textIndex, string queryText,
        string vectorIndex, ReadOnlySpan<float> queryVector, int k)
    {
        int dim = _tx.AsInternal().Access.TryGetVectorIndexSpec(vectorIndex, out var spec) ? spec.Dimensions : 0;
        var children = ImmutableArray.Create<LogicalOp>(
            new FullTextScanOp(null, textIndex, queryText, k, _stats?.FullTextCorpus(textIndex)),
            new KnnOp(null, vectorIndex, queryVector.ToArray(), k, dim));
        var plan = new FusionOp(children, k, FusionStrategy.Rrf);
        return new GraphTraversal<VertexId>(_tx, _schema, plan, row => row.GetVertexId(0), 0, aliases: null, stats: _stats);
    }

    // ── 重み付き最短経路 (Dijkstra / A*) ───────────────────────────────────────

    /// <summary>
    /// <paramref name="source"/> から <paramref name="target"/> までの
    /// <em>重み付き</em>最短経路を Dijkstra 法で求める。各エッジの重みは
    /// Edgeプロパティ <paramref name="weightKey"/> (数値型) から読む。
    /// プロパティを持たないエッジの重みは 1.0 として扱う。
    /// </summary>
    /// <remarks>
    /// ホップ数最短の <see cref="GraphTraversal{T}.ShortestPathTo"/> と異なり、
    /// 結果は重み合計が最小の経路を、距離 + Vertex列 + エッジ列として返す。
    /// 重みは非負でなければならない (負の重みを検出すると
    /// <see cref="InvalidOperationException"/>)。
    /// </remarks>
    /// <param name="source">始点Vertex。</param>
    /// <param name="target">終点Vertex。</param>
    /// <param name="weightKey">エッジ重みを保持するEdgeプロパティのキー名。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="type">辿るEdge型 (null なら全型)。</param>
    /// <param name="maxDistance">この重み合計を超える経路は探索しない (既定: 無制限)。</param>
    public WeightedPathResult WeightedShortestPath(
        VertexId source, VertexId target, string weightKey,
        Direction direction = Direction.Outgoing,
        string? type = null,
        double maxDistance = double.PositiveInfinity)
        => RunWeightedShortestPath(source, target, weightKey, direction, type, maxDistance, heuristic: null);

    /// <summary>
    /// ユーザ提供のヒューリスティック <paramref name="heuristic"/> を用いた A* 探索で
    /// 重み付き最短経路を求める。<paramref name="heuristic"/> は各Vertexから終点までの
    /// 推定残コストを返す。最適解を保証するには consistent (単調) かつ非負である必要がある。
    /// </summary>
    /// <param name="source">始点Vertex。</param>
    /// <param name="target">終点Vertex。</param>
    /// <param name="weightKey">エッジ重みを保持するEdgeプロパティのキー名。</param>
    /// <param name="heuristic">Vertex → 終点までの推定残コスト。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="type">辿るEdge型 (null なら全型)。</param>
    /// <param name="maxDistance">この重み合計を超える経路は探索しない (既定: 無制限)。</param>
    public WeightedPathResult WeightedShortestPath(
        VertexId source, VertexId target, string weightKey,
        Func<VertexId, double> heuristic,
        Direction direction = Direction.Outgoing,
        string? type = null,
        double maxDistance = double.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(heuristic);
        return RunWeightedShortestPath(
            source,
            target,
            weightKey,
            direction,
            type,
            maxDistance,
            new DelegateVertexHeuristic(heuristic));
    }

    /// <summary>
    /// Vertexの座標プロパティから自動生成したヒューリスティックを用いた A* 探索で
    /// 重み付き最短経路を求める。各Vertexの座標は数値プロパティ
    /// <paramref name="xKey"/> / <paramref name="yKey"/> から読む。
    /// </summary>
    /// <remarks>
    /// <see cref="HeuristicMetric.Euclidean"/> では平面距離、
    /// <see cref="HeuristicMetric.Haversine"/> では緯度経度 (度) からの大圏距離 (m) を
    /// 推定残コストとする。最適解を保証するには、ヒューリスティックがエッジ重みの単位で
    /// 実経路長の下界になっている必要がある (例: 重みが平面距離なら Euclidean、
    /// メートルの道路距離なら Haversine)。
    /// </remarks>
    /// <param name="source">始点Vertex。</param>
    /// <param name="target">終点Vertex。</param>
    /// <param name="weightKey">エッジ重みを保持するEdgeプロパティのキー名。</param>
    /// <param name="xKey">X 座標 (経度) を保持するVertexプロパティのキー名。</param>
    /// <param name="yKey">Y 座標 (緯度) を保持するVertexプロパティのキー名。</param>
    /// <param name="metric">座標から推定残コストを計算する距離尺度。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="type">辿るEdge型 (null なら全型)。</param>
    /// <param name="maxDistance">この重み合計を超える経路は探索しない (既定: 無制限)。</param>
    public WeightedPathResult WeightedShortestPathAStar(
        VertexId source, VertexId target, string weightKey,
        string xKey, string yKey,
        HeuristicMetric metric = HeuristicMetric.Euclidean,
        Direction direction = Direction.Outgoing,
        string? type = null,
        double maxDistance = double.PositiveInfinity)
    {
        ArgumentException.ThrowIfNullOrEmpty(xKey);
        ArgumentException.ThrowIfNullOrEmpty(yKey);

        if (!_schema.TryGetPropertyKeyId(xKey, out var xKeyId))
            throw MissingCoordinate(target, xKey);
        if (!_schema.TryGetPropertyKeyId(yKey, out var yKeyId))
            throw MissingCoordinate(target, yKey);

        double targetX = VertexCoordinate(target, xKey);
        double targetY = VertexCoordinate(target, yKey);
        ITransactionVertexHeuristic heuristic = new CoordinateHeuristic(
            xKeyId, yKeyId, xKey, yKey, targetX, targetY, metric);

        return RunWeightedShortestPath(source, target, weightKey, direction, type, maxDistance, heuristic);
    }

    private WeightedPathResult RunWeightedShortestPath(
        VertexId source, VertexId target, string weightKey,
        Direction direction, string? type, double maxDistance,
        ITransactionVertexHeuristic? heuristic)
    {
        ArgumentException.ThrowIfNullOrEmpty(weightKey);

        var keyId = _schema.TryGetPropertyKeyId(weightKey, out var resolvedKeyId)
            ? resolvedKeyId
            : PropertyKeyId.Invalid;
        var weightProvider = new PropertyChainWeightProvider(keyId);
        EdgeTypeId? typeId = null;
        if (type is not null)
        {
            if (!_schema.TryGetEdgeTypeId(type, out var resolvedTypeId))
                return WeightedPathResult.NotFound;

            typeId = resolvedTypeId;
        }

        var pair = new PairWithConstantOperator(new SingleVertexOperator(source), 0, target);
        var op = new WeightedShortestPathOperator(
            pair, 0, 1, direction, typeId, weightProvider, heuristic, maxDistance);

        using var result = _tx.Execute(op);
        foreach (var row in result.Rows())
        {
            WeightedPathCodec.Decode(row.GetBytes(3), out var vertices, out var edges);
            return new WeightedPathResult(true, row.GetDouble(2), vertices, edges);
        }
        return WeightedPathResult.NotFound;
    }

    private double VertexCoordinate(VertexId vertex, string key)
    {
        var v = _tx.GetProperty(vertex, key);
        return NumericCoordinate(vertex, key, in v);
    }

    private static double VertexCoordinate(
        ITransaction transaction,
        VertexId vertex,
        PropertyKeyId keyId,
        string key)
    {
        var properties = transaction.Vertices.EnumerateProperties(vertex, transaction.Properties);
        while (properties.MoveNext())
        {
            if (properties.Current.KeyId != keyId) continue;
            PropertyValue value = properties.Current.Value;
            return NumericCoordinate(vertex, key, in value);
        }
        throw MissingCoordinate(vertex, key);
    }

    private static double NumericCoordinate(VertexId vertex, string key, in PropertyValue v)
    {
        return v.Type switch
        {
            Storage.Records.PropertyValueType.Double => v.DoubleValue,
            Storage.Records.PropertyValueType.Int64 => v.Int64Value,
            Storage.Records.PropertyValueType.Int32 => v.Int32Value,
            _ => throw new InvalidOperationException(
                $"Vertex {vertex.Value} の座標プロパティ '{key}' が数値型ではありません (型: {v.Type})。"),
        };
    }

    private static InvalidOperationException MissingCoordinate(VertexId vertex, string key)
        => new($"Vertex {vertex.Value} の座標プロパティ '{key}' が設定されていません。");

    private sealed class CoordinateHeuristic(
        PropertyKeyId xKeyId,
        PropertyKeyId yKeyId,
        string xKey,
        string yKey,
        double targetX,
        double targetY,
        HeuristicMetric metric) : ITransactionVertexHeuristic
    {
        public double Estimate(ITransaction transaction, VertexId vertex)
        {
            double x = VertexCoordinate(transaction, vertex, xKeyId, xKey);
            double y = VertexCoordinate(transaction, vertex, yKeyId, yKey);
            if (metric == HeuristicMetric.Haversine)
                return Haversine(y, x, targetY, targetX);

            double dx = x - targetX;
            double dy = y - targetY;
            return Math.Sqrt(dx * dx + dy * dy);
        }
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

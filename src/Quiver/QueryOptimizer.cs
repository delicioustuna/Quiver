using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;

namespace Quiver;

/// <summary>オプティマイザが評価対象とする候補インデックスを表す。</summary>
public sealed record IndexCandidate(
    string IndexName,
    LabelId Label,
    long EstimatedRows);

/// <summary>多段トラバーサルプランの 1 ホップ。</summary>
public sealed record TraversalPlanStep(
    RelationshipTypeId? TypeFilter,
    Direction Direction);

/// <summary>オプティマイザが選択したスキャン種別。</summary>
public enum ScanKind
{
    /// <summary>全ノードスキャン。</summary>
    AllNodesScan,
    /// <summary>ラベル別スキャン。</summary>
    LabelScan,
    /// <summary>インデックスシーク。</summary>
    IndexSeek,
}

/// <summary>
/// オプティマイザが推奨できる展開ストラテジ。バックエンドの
/// <c>IGraphAccessMethods.Expand</c> 実装はこのヒントを無視して独自の access path を
/// 選ぶ自由がある。PW-17 で本格的なプランディスパッチを追加する。
/// </summary>
public enum ExpandStrategy
{
    /// <summary>ノード毎の隣接ブロック fast path + リンクリストフォールバック (バイナリバックエンドの既定)。</summary>
    AdjacencyBlock = 1,
    /// <summary>隣接ブロック fast path を使わず、リレーションシップリンクリスト (チェーン) を辿る。</summary>
    LinkedListChain = 2,
    /// <summary>PW-17 用に予約: シーケンシャルリレーションシップスキャン + frontier ビットセット probe。</summary>
    RelationshipScan = 3,
}

/// <summary><see cref="QueryOptimizer.SelectExpandPlan"/> が返す展開プラン。</summary>
public sealed record ExpandPlan(
    ExpandStrategy Strategy,
    double EstimatedFanOut)
{
    /// <summary>
    /// プランを物理オペレータとして実体化する。
    /// <see cref="ExpandStrategy.RelationshipScan"/> なら <see cref="RelationshipScanExpandOperator"/> を、
    /// それ以外は <see cref="ExpandOperator"/> を返す
    /// (バイナリバックエンドの access methods が内部で隣接ブロック / リンクリストを選ぶ)。
    /// </summary>
    public IPhysicalOperator Build(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        ExpandOutputMode outputMode)
        => Strategy switch
        {
            ExpandStrategy.RelationshipScan =>
                new RelationshipScanExpandOperator(source, sourceNodeColumn, direction, typeFilter, outputMode),
            _ => new ExpandOperator(source, sourceNodeColumn, direction, typeFilter, outputMode),
        };
}

/// <summary><see cref="QueryOptimizer.SelectScan"/> が返すスキャン決定。</summary>
public sealed record ScanPlan(
    ScanKind Kind,
    LabelId? Label,
    string? IndexName,
    long EstimatedRows)
{
    /// <summary>プランを物理オペレータとして実体化する。</summary>
    public IPhysicalOperator Build(ITupleProvider? indexKey = null) => Kind switch
    {
        ScanKind.IndexSeek when IndexName != null && indexKey != null
            => new NodeIndexSeekOperator(IndexName, indexKey),
        ScanKind.LabelScan when Label.HasValue
            => new NodeByLabelScanOperator(Label.Value),
        _ => new AllNodesScanOperator(Label),
    };
}

/// <summary>
/// コストベースクエリオプティマイザ。<see cref="GraphStats"/> を使って中間結果サイズを
/// 最小化するスキャンストラテジとトラバーサル順を選ぶ。
/// </summary>
public sealed class QueryOptimizer
{
    // 推定行数がラベル件数のこの比率を下回るときに IndexSeek を採用する。
    private const double IndexSelectivityThreshold = 0.05;
    // ナイーブな展開でこの件数を超える候補が出るときに双方向展開を選ぶ。
    private const double BidirectionalFanOutThreshold = 1_000.0;

    private readonly GraphStats _stats;

    /// <summary>指定した統計を背景に持つオプティマイザを生成する。</summary>
    public QueryOptimizer(GraphStats stats) => _stats = stats;

    // ---- スキャン選択 ----

    /// <summary>
    /// 任意のインデックス候補を考慮しつつ、ラベルに対して最も選択的なスキャンを選ぶ。
    /// ルール: IndexSeek &gt; LabelScan &gt; AllNodesScan。
    /// </summary>
    public ScanPlan SelectScan(LabelId? label, IReadOnlyList<IndexCandidate>? candidates = null)
    {
        if (candidates != null && candidates.Count > 0)
        {
            var best = candidates.MinBy(c => c.EstimatedRows)!;
            var labelCount = label.HasValue
                ? _stats.EstimateCardinality(label.Value)
                : _stats.TotalNodes;

            if (labelCount == 0 || (double)best.EstimatedRows / labelCount < IndexSelectivityThreshold)
                return new ScanPlan(ScanKind.IndexSeek, label, best.IndexName, best.EstimatedRows);
        }

        if (label.HasValue)
        {
            var count = _stats.EstimateCardinality(label.Value);
            return new ScanPlan(ScanKind.LabelScan, label, null, count);
        }

        return new ScanPlan(ScanKind.AllNodesScan, null, null, _stats.TotalNodes);
    }

    // ---- トラバーサル順最適化 ----

    /// <summary>
    /// 中間結果サイズを最小化するためにトラバーサルステップを並び替える。
    /// ルール: 推定 fan-out が小さいステップを先頭にまわす。
    /// </summary>
    public IReadOnlyList<TraversalPlanStep> OptimizeTraversal(IReadOnlyList<TraversalPlanStep> steps)
    {
        if (steps.Count <= 1) return steps;
        return [.. steps.OrderBy(EstimateFanOut)];
    }

    private double EstimateFanOut(TraversalPlanStep step)
        => _stats.EstimateFanOut(sourceLabel: null, step.TypeFilter, step.Direction);

    // ---- 展開プラン ----

    // PW-17: RelationshipScanExpandOperator が勝つのは frontier がほぼエッジ全体を覆う場合のみ。
    // ノード毎経路はリンクリスト / 隣接ブロック fast path の恩恵を受け、かつ各ソースの
    // 直接の隣接で停止するのに対し、スキャン経路は常に O(TotalRelationships) のコストを払う。
    // バイナリバックエンド (リンクリスト、隣接ブロックなし) では frontier カバー率約 85% でクロスオーバー、
    // 隣接ブロックあり構成ではクロスオーバーはさらに高くなる。
    // 詳細は docs/benchmarks/2026-05-15_PW-17_after.md。
    private const double RelationshipScanFrontierFraction = 0.85;

    /// <summary>
    /// frontier サイズが不明な 1 ホップ展開に対して <see cref="ExpandStrategy"/> を選ぶ。
    /// バイナリバックエンドが内部で扱う隣接ブロックストラテジを返す。
    /// プランナが frontier をマテリアライズ済み (BFS や多段チェーンなど) の場合は
    /// <paramref name="frontierSize"/> を取るオーバーロードを使うこと。
    /// </summary>
    public ExpandPlan SelectExpandPlan(
        LabelId? sourceLabel,
        RelationshipTypeId? typeFilter,
        Direction direction)
        => SelectExpandPlan(sourceLabel, typeFilter, direction, frontierSize: null);

    /// <summary>
    /// PW-17: 既知の <paramref name="frontierSize"/> から 1 ホップ展開向けに
    /// <see cref="ExpandStrategy"/> を選ぶ。<c>frontierSize * fanOut</c> がリレーションシップストアの
    /// 大部分に触れる見込みなら <see cref="ExpandStrategy.RelationshipScan"/> を、それ以外は
    /// <see cref="ExpandStrategy.AdjacencyBlock"/> (バイナリバックエンドはブロック未保有ノードでは
    /// 内部でリンクリストフォールバックに再委譲する) を選ぶ。
    /// </summary>
    public ExpandPlan SelectExpandPlan(
        LabelId? sourceLabel,
        RelationshipTypeId? typeFilter,
        Direction direction,
        long? frontierSize)
    {
        double fanOut = _stats.EstimateFanOut(sourceLabel, typeFilter, direction);

        if (frontierSize is long fs && fs > 0 && _stats.TotalRelationships > 0)
        {
            // ノード毎経路の総コストは fs * fanOut 回のリンクリスト / 隣接ブロック probe で、
            // 各 probe が cold ページを参照する可能性がある。スキャン経路は生存中の
            // リレーションシップページに 1 度ずつしか触れない。
            // ノード毎の probe 数がリレーションシップストアの一定割合を超えるところで切り替える。
            double estimatedProbes = fs * Math.Max(fanOut, 1.0);
            double threshold = _stats.TotalRelationships * RelationshipScanFrontierFraction;
            if (estimatedProbes >= threshold)
                return new ExpandPlan(ExpandStrategy.RelationshipScan, fanOut);
        }

        return new ExpandPlan(ExpandStrategy.AdjacencyBlock, fanOut);
    }

    // ---- PW-12: 述語順最適化 ----

    /// <summary>
    /// PW-12: 述語に紐付くヒント。<paramref name="EstimatedMatchingRows"/> はプランナによる
    /// 「この述語が残す入力行数」の推定値で、値が小さいほど選択的なので先に評価すべき。
    /// </summary>
    public readonly record struct PredicateCandidate(IPredicate Predicate, long EstimatedMatchingRows);

    /// <summary>
    /// PW-12: 推定マッチ行数の昇順で述語を並び替え、<see cref="BitmapFilterOperator"/> 内で
    /// 最も選択的な述語が先に評価されるようにする。タイブレークは安定順序を維持する。
    /// </summary>
    public static IReadOnlyList<IPredicate> OrderPredicatesBySelectivity(
        IReadOnlyList<PredicateCandidate> candidates)
    {
        if (candidates.Count <= 1)
            return candidates.Count == 0 ? Array.Empty<IPredicate>() : new[] { candidates[0].Predicate };
        var indexed = new (PredicateCandidate Cand, int Idx)[candidates.Count];
        for (int i = 0; i < candidates.Count; i++) indexed[i] = (candidates[i], i);
        Array.Sort(indexed, (a, b) =>
        {
            int c = a.Cand.EstimatedMatchingRows.CompareTo(b.Cand.EstimatedMatchingRows);
            return c != 0 ? c : a.Idx.CompareTo(b.Idx);
        });
        var result = new IPredicate[candidates.Count];
        for (int i = 0; i < indexed.Length; i++) result[i] = indexed[i].Cand.Predicate;
        return result;
    }

    /// <summary>
    /// PW-12: <see cref="OrderPredicatesBySelectivity"/> で並び替えた述語列を持つ
    /// <see cref="BitmapFilterOperator"/> を構築する。選択度推定はオプティマイザの自由で、
    /// 述語毎統計を持たない呼び出し側は <c>EstimatedMatchingRows</c>=0 を渡して入力順を維持できる。
    /// </summary>
    public static IPhysicalOperator BuildBitmapFilter(
        IPhysicalOperator source,
        IReadOnlyList<PredicateCandidate> candidates)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("少なくとも 1 つの述語が必要です。", nameof(candidates));
        return new BitmapFilterOperator(source, OrderPredicatesBySelectivity(candidates));
    }

    // ---- 高次数枝刈り ----

    /// <summary>
    /// <paramref name="hopCount"/> ホップのナイーブな前方展開が候補を出しすぎる場合に
    /// 双方向展開を推奨する。ルール: (meanDegree ^ hopCount) &gt; threshold。
    /// </summary>
    public bool ShouldUseBidirectional(LabelId startLabel, int hopCount)
    {
        if (hopCount < 2) return false;
        var meanDegree = _stats.EstimateMeanDegree(startLabel);
        return Math.Pow(meanDegree, hopCount) > BidirectionalFanOutThreshold;
    }

    // ---- VEC-6: KNN ストラテジ選択 ----

    /// <summary>
    /// ベクトルインデックス全体に対する候補集合の比率がこの値を下回ると graph-first が有利になる。
    /// 候補集合がインデックス全体に対して十分小さければ、候補毎のベクトル lookup の方が
    /// KNN のオーバーサンプリングより安価になるため。
    /// </summary>
    private const double KnnGraphFirstFraction = 0.05;

    /// <summary>
    /// VEC-6: クエリに KNN とグラフ制約の両方が現れるとき、vector-first / graph-first /
    /// hybrid rerank を選ぶ。オペレータはどちらの順序も正しく処理できるため、
    /// オプティマイザが誤っても結果は正しいが、適切な選択により無駄なスコアリングを大幅に削減できる。
    /// </summary>
    /// <param name="candidateCount">
    /// グラフ / プロパティ制約側 (label + Has + 近傍) を満たすノードの推定件数。
    /// 不明な場合は 0 を渡すと、オプティマイザは vector-first をデフォルト採用する。
    /// </param>
    /// <param name="k">KNN 側から要求された top-k。</param>
    /// <param name="totalIndexedCount">
    /// ベクトルインデックスのサイズ (通常は <c>db.Vectors</c> のエントリ数)。
    /// 厳密な件数を把握できない場合は <c>GraphStats.TotalNodes</c> を渡す。
    /// </param>
    public KnnStrategy ChooseKnnStrategy(long candidateCount, int k, long totalIndexedCount)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));

        // グラフ側の知識が無い → KNN 駆動と仮定する。
        if (candidateCount <= 0 || totalIndexedCount <= 0)
            return KnnStrategy.VectorFirst;

        // graph-first が意味を持つのは候補集合が十分小さく、全員に触れる方が KNN を
        // オーバーサンプリングするより安価になる場合に限る。2k 件以下なら常に graph-first を選ぶ
        // (どのみち少なくとも 4k はオーバーサンプリングするため)。
        if (candidateCount <= Math.Max(2L * k, 16))
            return KnnStrategy.GraphFirst;

        double fraction = (double)candidateCount / totalIndexedCount;
        if (fraction < KnnGraphFirstFraction)
            return KnnStrategy.GraphFirst;

        // 中間域: hybrid rerank は将来の拡張点。現状は vector-first (安価で挙動が読みやすい) を
        // 選ぶが、Hybrid を返却口として公開しておくことで、将来カスタムプランが入った際に
        // 呼び出し側がオプトインできるようにする。
        if (fraction < 0.5)
            return KnnStrategy.VectorFirst;

        return KnnStrategy.VectorFirst;
    }

    // ---- 便利アクセサ ----

    /// <summary>指定ラベルの推定カーディナリティを返す。</summary>
    public long EstimateCardinality(LabelId label) => _stats.EstimateCardinality(label);

    /// <summary>指定ラベルの推定平均 degree を返す。</summary>
    public double EstimateMeanDegree(LabelId label) => _stats.EstimateMeanDegree(label);

    /// <summary>背景の <see cref="GraphStats"/>。</summary>
    public GraphStats Stats => _stats;
}

/// <summary>
/// VEC-6: グラフ制約と KNN の評価順を表すストラテジ。vector-first は KNN top-k を取ってから
/// フィルタを適用、graph-first は候補集合を計算してから KNN-within-set を求め、
/// hybrid は将来のスコアリランクプラン拡張点として予約。
/// </summary>
public enum KnnStrategy
{
    /// <summary>KNN を先に評価する。</summary>
    VectorFirst = 1,
    /// <summary>グラフ側候補を先に評価する。</summary>
    GraphFirst = 2,
    /// <summary>(将来予約) スコアリランクハイブリッド。</summary>
    Hybrid = 3,
}

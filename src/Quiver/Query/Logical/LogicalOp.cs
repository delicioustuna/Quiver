using System.Collections.Immutable;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Query.Logical;

/// <summary>
/// 単一論理プラン代数 (LogicalPlan IR) のノード基底。
/// fluent DSL / Match / 将来の Cypher・Gremlin parser はすべて <see cref="LogicalOp"/> ツリーへ
/// lower し、<c>LogicalOptimizer</c> が rule + cost で書き換え、<c>PhysicalPlanner</c> が
/// <see cref="IPhysicalOperator"/> へ落とす。実行は現行 pull 型を踏襲する。
/// </summary>
/// <remarks>
/// 各ノードは <see cref="CurrentEntityColumn"/> / <see cref="PredictedOutputColumnCount"/> の
/// 論理シェイプメタデータを持つ。これは as/select エイリアスと carry 列計算を DSL 側で
/// 継続するためで、旧 <c>IOperatorBuilder</c> が担っていた役割をそのまま引き継ぐ。
/// </remarks>
internal abstract record LogicalOp
{
    /// <summary>このノードが放出するタプルのうち「カレントエンティティ」を保持する列番号。</summary>
    public abstract int CurrentEntityColumn { get; }

    /// <summary>このノードが放出するタプルの列数 (予測)。</summary>
    public abstract int PredictedOutputColumnCount { get; }
}

/// <summary>
/// 全件スキャン起点。<see cref="EntityKind.Node"/> + <see cref="Label"/>=null で全ノード、
/// label 指定でラベル別スキャン、<see cref="EntityKind.Relationship"/> で全リレーションシップ。
/// label は lowering 時に解決済みの <see cref="LabelId"/> を保持する (planner は <c>NodeByLabelScan</c> へ直結)。
/// </summary>
internal sealed record ScanOp(EntityKind Kind, LabelId? Label) : LogicalOp
{
    public override int CurrentEntityColumn => 0;
    public override int PredictedOutputColumnCount => 1;
}

/// <summary>定数ノード起点 (<c>g.Node(id)</c> / <c>g.Nodes(ids)</c>)。</summary>
internal sealed record NodeSeedOp(NodeId[] Ids) : LogicalOp
{
    public override int CurrentEntityColumn => 0;
    public override int PredictedOutputColumnCount => 1;
}

/// <summary>
/// 相関サブクエリ / 分岐の probe 起点。物理化時に <see cref="Probe"/> をそのまま用い、
/// 外側オペレータがバインドする (現 <c>CorrelatedSeedBuilder</c> と同形)。
/// </summary>
internal sealed record CorrelatedInputOp(CorrelatedInputOperator Probe) : LogicalOp
{
    public override int CurrentEntityColumn => 0;
    public override int PredictedOutputColumnCount => 1;
}

/// <summary>述語フィルタ。<see cref="PredicateFactory"/> は物理化時に schema を受け取り述語を作る。</summary>
internal sealed record FilterOp(LogicalOp Source, Func<ISchemaApi, IPredicate> PredicateFactory) : LogicalOp
{
    public override int CurrentEntityColumn => Source.CurrentEntityColumn;
    public override int PredictedOutputColumnCount => Source.PredictedOutputColumnCount;
}

/// <summary>1 ホップ展開。<see cref="Mode"/> に応じて 1〜3 列 + carry を放出する。</summary>
internal sealed record ExpandOp(
    LogicalOp Source,
    int SourceColumn,
    Direction Direction,
    string? Type,
    ExpandOutputMode Mode,
    int[]? Carry) : LogicalOp
{
    private int BaseColumnCount => Mode switch
    {
        ExpandOutputMode.NeighborOnly   => 1,
        ExpandOutputMode.NeighborAndRel => 2,
        _                               => 3,
    };

    public override int CurrentEntityColumn => Mode switch
    {
        ExpandOutputMode.NeighborOnly   => 0,
        ExpandOutputMode.NeighborAndRel => 1,
        _ /* Full */                    => 2,
    };

    public override int PredictedOutputColumnCount => BaseColumnCount + (Carry?.Length ?? 0);
}

/// <summary>可変長展開 (<c>Repeat</c>)。(startNode, endNode) を放出し endNode が列 1。</summary>
internal sealed record VarLenExpandOp(
    LogicalOp Source,
    Direction Direction,
    string? Type,
    int MinHops,
    int MaxHops) : LogicalOp
{
    public override int CurrentEntityColumn => 1;
    public override int PredictedOutputColumnCount => 2;
}

/// <summary>ホップ数最短経路 (<c>ShortestPathTo</c>)。(source, target, distance) を放出し distance が列 2。</summary>
internal sealed record PathOp(
    LogicalOp Source,
    NodeId Target,
    Direction Direction,
    string? Type,
    long MaxDistance) : LogicalOp
{
    public override int CurrentEntityColumn => 2;
    public override int PredictedOutputColumnCount => 3;
}

/// <summary>
/// KNN ベクトル検索起点。<see cref="Candidate"/>=null で vector-first (KNN top-k を直接放出)、
/// <see cref="Candidate"/>!=null で graph-first (候補集合内 KNN)。DSL は常に
/// <see cref="Candidate"/>=null で生成し、<c>LogicalOptimizer</c> の KnnPushdown rule が
/// 構造ヒント + 統計から候補を確定する。
/// </summary>
internal sealed record KnnOp(
    LogicalOp? Candidate,
    string IndexName,
    float[] Query,
    int K,
    int Dim) : LogicalOp
{
    public override int CurrentEntityColumn => 0;
    public override int PredictedOutputColumnCount => 1;
}

/// <summary>
/// BM25 全文検索起点。<see cref="Candidate"/>=null で text-first (BM25 top-k を直接放出)、
/// <see cref="Candidate"/>!=null で graph-first (候補集合内 BM25)。DSL <c>g.Search</c> は常に
/// <see cref="Candidate"/>=null で生成し <c>LogicalOptimizer</c> の FullTextPushdown rule が候補を確定する。
/// <c>.FilterByText</c> は <see cref="Candidate"/> を直接据えた graph-first を生成する。KnnOp と相似形。
/// <see cref="Corpus"/> は GraphStats から拾った N/avgdl スナップショット。null なら
/// operator が norms 索引から概算する。push-down rewrite を跨いで保持される。
/// </summary>
internal sealed record FullTextScanOp(
    LogicalOp? Candidate,
    string IndexName,
    string QueryText,
    int K,
    Bm25CorpusStats? Corpus = null) : LogicalOp
{
    public override int CurrentEntityColumn => 0;
    public override int PredictedOutputColumnCount => 1;
}

/// <summary>融合戦略。Phase 1 は RRF (Reciprocal Rank Fusion) のみ。enum は将来拡張用。</summary>
internal enum FusionStrategy
{
    /// <summary>Reciprocal Rank Fusion: <c>Σ_i 1/(k0 + rank_i(d))</c> (k0=60)。rank のみで score 配管不要。</summary>
    Rrf,
}

/// <summary>
/// ランク融合起点。各 <see cref="Children"/> は ranked top-k を産む leaf 検索
/// (<see cref="FullTextScanOp"/> / <see cref="KnnOp"/>) で、それらの順位を <see cref="Strategy"/>
/// (Phase 1 は RRF) で融合し上位 <see cref="K"/> 件を放出する。RRF は rank のみで計算できるため
/// 既存の「score 非公開」設計 (KnnOp / FullTextScanOp と同方針) を変えずに融合できる
/// 。DSL (<c>g.HybridSearch</c>) は子を常に <c>Candidate=null</c> で生成する。
/// </summary>
internal sealed record FusionOp(
    ImmutableArray<LogicalOp> Children,
    int K,
    FusionStrategy Strategy) : LogicalOp
{
    public override int CurrentEntityColumn => 0;
    public override int PredictedOutputColumnCount => 1;
}

/// <summary>プロパティ値を末尾列へマテリアライズする (<c>Values</c> / 集約 row path / sort key)。</summary>
internal sealed record PropertyLookupOp(LogicalOp Source, string Key, EntityKind Kind) : LogicalOp
{
    public override int CurrentEntityColumn => Source.CurrentEntityColumn;
    public override int PredictedOutputColumnCount => Source.PredictedOutputColumnCount + 1;
}

/// <summary>ラベル名を末尾列へマテリアライズする (<c>.label()</c>)。</summary>
internal sealed record LabelNameLookupOp(LogicalOp Source, int NodeColumn) : LogicalOp
{
    public override int CurrentEntityColumn => Source.CurrentEntityColumn;
    public override int PredictedOutputColumnCount => Source.PredictedOutputColumnCount + 1;
}

/// <summary>リレーションシップ列をノード列へ解決する (<c>.outV()</c> / <c>.inV()</c> / <c>.otherV()</c>)。</summary>
internal sealed record RelationshipEndpointOp(LogicalOp Source, int RelColumn, RelationshipEndpoint Endpoint) : LogicalOp
{
    public override int CurrentEntityColumn => 0;
    public override int PredictedOutputColumnCount => 1;
}

/// <summary>skip / limit ウィンドウ。</summary>
internal sealed record LimitOp(LogicalOp Source, long Limit, long Skip) : LogicalOp
{
    public override int CurrentEntityColumn => Source.CurrentEntityColumn;
    public override int PredictedOutputColumnCount => Source.PredictedOutputColumnCount;
}

/// <summary>
/// ソート。<see cref="PropertyKey"/> != null ならプロパティ値を <see cref="SortColumn"/> へ
/// マテリアライズしてからソートする (列が 1 つ増える)。null なら既存 <see cref="SortColumn"/> で直接ソート。
/// </summary>
internal sealed record SortOp(LogicalOp Source, string? PropertyKey, int SortColumn, bool Descending) : LogicalOp
{
    public override int CurrentEntityColumn => Source.CurrentEntityColumn;
    public override int PredictedOutputColumnCount =>
        PropertyKey != null ? Source.PredictedOutputColumnCount + 1 : Source.PredictedOutputColumnCount;
}

/// <summary>単一キー列の重複排除 (<c>.dedup()</c>)。</summary>
internal sealed record DedupOp(LogicalOp Source, int KeyColumn) : LogicalOp
{
    public override int CurrentEntityColumn => Source.CurrentEntityColumn;
    public override int PredictedOutputColumnCount => Source.PredictedOutputColumnCount;
}

/// <summary>相関分岐の種別。</summary>
internal enum LogicalBranchKind
{
    Union,
    Coalesce,
    Optional,
}

/// <summary>
/// 相関分岐 (<c>.union</c> / <c>.coalesce</c> / <c>.optional</c>)。物理 probe/branch の生成は
/// 実行エンジンを作り直さないため不透明クロージャ <see cref="BuildBranches"/> として保持し、
/// planner 段で評価する (現 <c>BranchedBuilder</c> と同形)。
/// </summary>
internal sealed record BranchOp(
    LogicalOp Source,
    Func<ISchemaApi, (CorrelatedInputOperator[] Probes, IPhysicalOperator[] Branches)> BuildBranches,
    LogicalBranchKind Kind) : LogicalOp
{
    public override int CurrentEntityColumn => 0;
    public override int PredictedOutputColumnCount => 1;
}

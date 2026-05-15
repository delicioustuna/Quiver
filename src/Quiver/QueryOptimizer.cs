using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;

namespace Quiver;

/// <summary>Describes a candidate index for the optimizer to evaluate.</summary>
public sealed record IndexCandidate(
    string IndexName,
    LabelId Label,
    long EstimatedRows);

/// <summary>One hop in a multi-hop traversal plan.</summary>
public sealed record TraversalPlanStep(
    RelationshipTypeId? TypeFilter,
    Direction Direction);

/// <summary>The kind of scan the optimizer selected.</summary>
public enum ScanKind { AllNodesScan, LabelScan, IndexSeek }

/// <summary>
/// Expansion strategies the optimizer can recommend. The backend's
/// <c>IGraphAccessMethods.Expand</c> implementation is free to ignore the
/// hint and pick its own access path; PW-17 will add real plan dispatch.
/// </summary>
public enum ExpandStrategy
{
    /// <summary>Per-node adjacency-block fast path with linked-list fallback (default for binary backend).</summary>
    AdjacencyBlock = 1,
    /// <summary>Walk the relationship linked list (chain) without the adjacency block fast path.</summary>
    LinkedListChain = 2,
    /// <summary>Reserved for PW-17: sequential relationship scan + frontier bitset probe.</summary>
    RelationshipScan = 3,
}

/// <summary>Expansion plan returned by <see cref="QueryOptimizer.SelectExpandPlan"/>.</summary>
public sealed record ExpandPlan(
    ExpandStrategy Strategy,
    double EstimatedFanOut)
{
    /// <summary>
    /// Materialise the plan as a physical operator. For
    /// <see cref="ExpandStrategy.RelationshipScan"/> we emit
    /// <see cref="RelationshipScanExpandOperator"/>; the other strategies are
    /// served by <see cref="ExpandOperator"/> (the binary backend's access
    /// methods pick adjacency block vs linked-list internally).
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

/// <summary>Scan decision returned by <see cref="QueryOptimizer.SelectScan"/>.</summary>
public sealed record ScanPlan(
    ScanKind Kind,
    LabelId? Label,
    string? IndexName,
    long EstimatedRows)
{
    /// <summary>Materialise the plan as a physical operator.</summary>
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
/// Cost-based query optimizer. Uses <see cref="GraphStats"/> to choose scan strategies
/// and traversal orderings that minimise intermediate result sizes.
/// </summary>
public sealed class QueryOptimizer
{
    // Use IndexSeek when its estimated row count is below this fraction of the label count.
    private const double IndexSelectivityThreshold = 0.05;
    // Prefer bidirectional expansion when naive expansion would exceed this many candidates.
    private const double BidirectionalFanOutThreshold = 1_000.0;

    private readonly GraphStats _stats;

    public QueryOptimizer(GraphStats stats) => _stats = stats;

    // ---- Scan selection ----

    /// <summary>
    /// Choose the most selective scan for a label with optional index candidates.
    /// Rule: IndexSeek > LabelScan > AllNodesScan.
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

    // ---- Traversal ordering ----

    /// <summary>
    /// Reorder traversal steps to minimise intermediate result sizes.
    /// Rule: steps with lower estimated fan-out come first.
    /// </summary>
    public IReadOnlyList<TraversalPlanStep> OptimizeTraversal(IReadOnlyList<TraversalPlanStep> steps)
    {
        if (steps.Count <= 1) return steps;
        return [.. steps.OrderBy(EstimateFanOut)];
    }

    private double EstimateFanOut(TraversalPlanStep step)
        => _stats.EstimateFanOut(sourceLabel: null, step.TypeFilter, step.Direction);

    // ---- Expansion plan ----

    // PW-17: RelationshipScanExpandOperator only wins once the frontier covers
    // almost the whole edge set, because the per-node path benefits from
    // linked-list / adjacency-block fast paths plus stops at each source's
    // immediate neighbours, whereas the scan path is always O(TotalRelationships).
    // Measured crossover with the binary backend (linked-list, no adjacency
    // blocks) was around 85% frontier coverage; with adjacency blocks the
    // crossover is even higher. See docs/benchmarks/2026-05-15_PW-17_after.md.
    private const double RelationshipScanFrontierFraction = 0.85;

    /// <summary>
    /// Pick an <see cref="ExpandStrategy"/> for a one-hop expansion when the
    /// optimizer does not know the upcoming frontier size. Returns the
    /// adjacency-block strategy that the binary backend handles internally.
    /// Use the overload taking <paramref name="frontierSize"/> when the planner
    /// already materialised the frontier (e.g. BFS, multi-hop chain).
    /// </summary>
    public ExpandPlan SelectExpandPlan(
        LabelId? sourceLabel,
        RelationshipTypeId? typeFilter,
        Direction direction)
        => SelectExpandPlan(sourceLabel, typeFilter, direction, frontierSize: null);

    /// <summary>
    /// PW-17: Pick an <see cref="ExpandStrategy"/> for a one-hop expansion given a
    /// known <paramref name="frontierSize"/>. Picks <see cref="ExpandStrategy.RelationshipScan"/>
    /// when <c>frontierSize * fanOut</c> would touch a large fraction of the
    /// relationship store, otherwise falls back to <see cref="ExpandStrategy.AdjacencyBlock"/>
    /// (which the binary backend itself further refines to a linked-list fallback
    /// when no block exists for a given node).
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
            // Total work for the per-node path is roughly fs * fanOut linked-list /
            // adjacency-block probes, each chasing potentially cold pages. The
            // scan path touches every live relationship page exactly once. We
            // switch when the per-node probe count exceeds a meaningful slice of
            // the relationship store.
            double estimatedProbes = fs * Math.Max(fanOut, 1.0);
            double threshold = _stats.TotalRelationships * RelationshipScanFrontierFraction;
            if (estimatedProbes >= threshold)
                return new ExpandPlan(ExpandStrategy.RelationshipScan, fanOut);
        }

        return new ExpandPlan(ExpandStrategy.AdjacencyBlock, fanOut);
    }

    // ---- High-degree pruning ----

    /// <summary>
    /// Recommend bidirectional expansion when the naive forward fan-out for
    /// <paramref name="hopCount"/> hops would produce too many candidates.
    /// Rule: (meanDegree ^ hopCount) > threshold.
    /// </summary>
    public bool ShouldUseBidirectional(LabelId startLabel, int hopCount)
    {
        if (hopCount < 2) return false;
        var meanDegree = _stats.EstimateMeanDegree(startLabel);
        return Math.Pow(meanDegree, hopCount) > BidirectionalFanOutThreshold;
    }

    // ---- VEC-6: KNN strategy selection ----

    /// <summary>
    /// Fraction of the vector index below which graph-first becomes
    /// attractive — the per-candidate vector lookup beats oversampling KNN
    /// once the candidate set is tiny relative to the whole index.
    /// </summary>
    private const double KnnGraphFirstFraction = 0.05;

    /// <summary>
    /// VEC-6: choose between vector-first, graph-first, and hybrid rerank
    /// when KNN and graph constraints both appear in a query. The optimizer
    /// is allowed to be wrong — operators handle either order correctly —
    /// but a good choice trims a lot of unnecessary scoring.
    /// </summary>
    /// <param name="candidateCount">
    /// Estimated number of nodes that satisfy the graph / property
    /// constraint side (label + Has + neighborhood). Pass 0 when unknown;
    /// the optimizer defaults to vector-first in that case.
    /// </param>
    /// <param name="k">Top-k requested by the KNN side.</param>
    /// <param name="totalIndexedCount">
    /// Size of the vector index (typically <c>db.Vectors</c> entry count).
    /// Pass <c>GraphStats.TotalNodes</c> when an exact count isn't handy.
    /// </param>
    public KnnStrategy ChooseKnnStrategy(long candidateCount, int k, long totalIndexedCount)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));

        // No graph-side knowledge → assume KNN should drive.
        if (candidateCount <= 0 || totalIndexedCount <= 0)
            return KnnStrategy.VectorFirst;

        // Graph-first only makes sense when the candidate set is small enough
        // that touching every member is cheaper than oversampling KNN. Below
        // 2k we always want graph-first (we'd oversample at least 4k anyway).
        if (candidateCount <= Math.Max(2L * k, 16))
            return KnnStrategy.GraphFirst;

        double fraction = (double)candidateCount / totalIndexedCount;
        if (fraction < KnnGraphFirstFraction)
            return KnnStrategy.GraphFirst;

        // Mid-range: hybrid rerank is a future hook. For now we still pick
        // vector-first (cheap, well-understood) but surface Hybrid so callers
        // can opt into a custom plan when one lands.
        if (fraction < 0.5)
            return KnnStrategy.VectorFirst;

        return KnnStrategy.VectorFirst;
    }

    // ---- Convenience accessors ----

    public long EstimateCardinality(LabelId label) => _stats.EstimateCardinality(label);
    public double EstimateMeanDegree(LabelId label) => _stats.EstimateMeanDegree(label);
    public GraphStats Stats => _stats;
}

/// <summary>
/// VEC-6: plan ordering between graph constraints and KNN. The vector-first
/// path runs KNN top-k then applies filters; graph-first computes the
/// candidate set then asks the vector index for KNN-within-set; hybrid
/// reserves space for a future score-rerank plan.
/// </summary>
public enum KnnStrategy
{
    VectorFirst = 1,
    GraphFirst = 2,
    Hybrid = 3,
}

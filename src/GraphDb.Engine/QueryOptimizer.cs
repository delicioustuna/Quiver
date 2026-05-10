using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine;

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
    {
        if (!step.TypeFilter.HasValue)
            return _stats.GlobalDegreeHistogram.MeanDegree;

        var edgeCount = _stats.EdgeTypeFrequency.TryGetValue(step.TypeFilter.Value, out var c) ? c : 0L;
        return _stats.TotalNodes == 0 ? 0.0 : (double)edgeCount / _stats.TotalNodes;
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

    // ---- Convenience accessors ----

    public long EstimateCardinality(LabelId label) => _stats.EstimateCardinality(label);
    public double EstimateMeanDegree(LabelId label) => _stats.EstimateMeanDegree(label);
    public GraphStats Stats => _stats;
}

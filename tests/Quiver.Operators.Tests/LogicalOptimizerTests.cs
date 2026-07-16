using FluentAssertions;
using Quiver;
using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical.Tests.Support;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public sealed class LogicalOptimizerTests
{
    private readonly StubSchemaApi _schema = new();

    // ── LabelScanRewrite ────────────────────────────────────────────────────────

    [Fact]
    public void LabelScanRewrite_folds_label_filter_into_ScanOp()
    {
        var labelId = _schema.GetOrCreateLabel("Person");
        var scan = new ScanOp(EntityKind.Vertex, null);
        var filter = new FilterOp(scan, s => new LabelPredicate(s.GetOrCreateLabel("Person")));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        result.Should().BeOfType<ScanOp>()
            .Which.Label.Should().Be(labelId);
    }

    [Fact]
    public void LabelScanRewrite_does_not_fold_when_scan_already_has_label()
    {
        var existingLabel = new LabelId(99);
        var scan = new ScanOp(EntityKind.Vertex, existingLabel);
        var filter = new FilterOp(scan, s => new LabelPredicate(s.GetOrCreateLabel("Other")));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        result.Should().BeOfType<FilterOp>();
    }

    [Fact]
    public void LabelScanRewrite_does_not_fold_edge_scan()
    {
        var scan = new ScanOp(EntityKind.Edge, null);
        var filter = new FilterOp(scan, s => new LabelPredicate(s.GetOrCreateLabel("TYPE")));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        result.Should().BeOfType<FilterOp>();
    }

    [Fact]
    public void LabelScanRewrite_does_not_fold_non_col0_label_predicate()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var filter = new FilterOp(scan, s => new LabelPredicate(s.GetOrCreateLabel("Person"), column: 1));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        result.Should().BeOfType<FilterOp>();
    }

    // ── KnnPushdown ─────────────────────────────────────────────────────────────

    [Fact]
    public void KnnPushdown_with_label_filter_and_no_stats_produces_graph_first()
    {
        var knn = new KnnOp(null, "idx", new float[] { 1 }, 5, 128);
        var filter = new FilterOp(knn, s => new LabelPredicate(s.GetOrCreateLabel("Doc")));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        result.Should().BeOfType<KnnOp>()
            .Which.Candidate.Should().NotBeNull();
    }

    [Fact]
    public void KnnPushdown_graph_first_candidate_gets_LabelScanRewrite()
    {
        var knn = new KnnOp(null, "idx", new float[] { 1 }, 5, 128);
        var filter = new FilterOp(knn, s => new LabelPredicate(s.GetOrCreateLabel("Doc")));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        var knnResult = result.Should().BeOfType<KnnOp>().Subject;
        knnResult.Candidate.Should().BeOfType<ScanOp>()
            .Which.Label.Should().NotBeNull();
    }

    [Fact]
    public void KnnPushdown_no_filters_leaves_KnnOp_unchanged()
    {
        var knn = new KnnOp(null, "idx", new float[] { 1 }, 5, 128);

        var result = LogicalOptimizer.Optimize(knn, null, _schema);

        result.Should().BeOfType<KnnOp>()
            .Which.Candidate.Should().BeNull();
    }

    [Fact]
    public void KnnPushdown_high_cardinality_label_stays_vector_first()
    {
        var labelId = _schema.GetOrCreateLabel("Common");
        var stats = GraphStats.ForTest(1000,
            new Dictionary<LabelId, long> { [labelId] = 500 });

        var knn = new KnnOp(null, "idx", new float[] { 1 }, 5, 128);
        var filter = new FilterOp(knn, s => new LabelPredicate(s.GetOrCreateLabel("Common")));

        var result = LogicalOptimizer.Optimize(filter, stats, _schema);

        // 50% >= 30% threshold => vector-first (filters stay as post-filters)
        result.Should().BeOfType<FilterOp>();
        var innerKnn = result.Should().BeOfType<FilterOp>().Subject.Source;
        innerKnn.Should().BeOfType<KnnOp>()
            .Which.Candidate.Should().BeNull();
    }

    [Fact]
    public void KnnPushdown_low_cardinality_label_produces_graph_first()
    {
        var labelId = _schema.GetOrCreateLabel("Rare");
        var stats = GraphStats.ForTest(10000,
            new Dictionary<LabelId, long> { [labelId] = 100 });

        var knn = new KnnOp(null, "idx", new float[] { 1 }, 5, 128);
        var filter = new FilterOp(knn, s => new LabelPredicate(s.GetOrCreateLabel("Rare")));

        var result = LogicalOptimizer.Optimize(filter, stats, _schema);

        // 1% < 30% threshold => graph-first
        result.Should().BeOfType<KnnOp>()
            .Which.Candidate.Should().NotBeNull();
    }

    // ── KnnLimitPushdown ────────────────────────────────────────────────────────

    [Fact]
    public void KnnLimitPushdown_shrinks_K_and_removes_Limit()
    {
        var knn = new KnnOp(null, "idx", new float[] { 1 }, 100, 128);
        var limit = new LimitOp(knn, 5, 0);

        var result = LogicalOptimizer.Optimize(limit, null, _schema);

        result.Should().BeOfType<KnnOp>()
            .Which.K.Should().Be(5);
    }

    [Fact]
    public void KnnLimitPushdown_does_not_shrink_when_K_already_smaller()
    {
        var knn = new KnnOp(null, "idx", new float[] { 1 }, 3, 128);
        var limit = new LimitOp(knn, 10, 0);

        var result = LogicalOptimizer.Optimize(limit, null, _schema);

        result.Should().BeOfType<KnnOp>()
            .Which.K.Should().Be(3);
    }

    [Fact]
    public void KnnLimitPushdown_does_not_apply_when_skip_is_nonzero()
    {
        var knn = new KnnOp(null, "idx", new float[] { 1 }, 100, 128);
        var limit = new LimitOp(knn, 5, 2);

        var result = LogicalOptimizer.Optimize(limit, null, _schema);

        result.Should().BeOfType<LimitOp>();
    }

    // ── FullTextPushdown ────────────────────────────────────────────────────────

    [Fact]
    public void FullTextPushdown_with_label_filter_and_no_stats_produces_graph_first()
    {
        var ft = new FullTextScanOp(null, "ft_idx", "hello", 10);
        var filter = new FilterOp(ft, s => new LabelPredicate(s.GetOrCreateLabel("Article")));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        result.Should().BeOfType<FullTextScanOp>()
            .Which.Candidate.Should().NotBeNull();
    }

    [Fact]
    public void FullTextPushdown_no_filters_leaves_FullTextScanOp_unchanged()
    {
        var ft = new FullTextScanOp(null, "ft_idx", "hello", 10);

        var result = LogicalOptimizer.Optimize(ft, null, _schema);

        result.Should().BeOfType<FullTextScanOp>()
            .Which.Candidate.Should().BeNull();
    }

    [Fact]
    public void FullTextPushdown_high_cardinality_stays_text_first()
    {
        var labelId = _schema.GetOrCreateLabel("Bulk");
        var stats = GraphStats.ForTest(1000,
            new Dictionary<LabelId, long> { [labelId] = 400 });

        var ft = new FullTextScanOp(null, "ft_idx", "hello", 10);
        var filter = new FilterOp(ft, s => new LabelPredicate(s.GetOrCreateLabel("Bulk")));

        var result = LogicalOptimizer.Optimize(filter, stats, _schema);

        // 40% >= 30% threshold => text-first (filter stays as post-filter)
        result.Should().BeOfType<FilterOp>();
    }

    [Fact]
    public void FullTextPushdown_low_cardinality_produces_graph_first()
    {
        var labelId = _schema.GetOrCreateLabel("Sparse");
        var stats = GraphStats.ForTest(10000,
            new Dictionary<LabelId, long> { [labelId] = 50 });

        var ft = new FullTextScanOp(null, "ft_idx", "hello", 10);
        var filter = new FilterOp(ft, s => new LabelPredicate(s.GetOrCreateLabel("Sparse")));

        var result = LogicalOptimizer.Optimize(filter, stats, _schema);

        // 0.5% < 30% threshold => graph-first
        result.Should().BeOfType<FullTextScanOp>()
            .Which.Candidate.Should().NotBeNull();
    }

    // ── FullTextLimitPushdown ────────────────────────────────────────────────────

    [Fact]
    public void FullTextLimitPushdown_shrinks_K_and_removes_Limit()
    {
        var ft = new FullTextScanOp(null, "ft_idx", "hello", 100);
        var limit = new LimitOp(ft, 5, 0);

        var result = LogicalOptimizer.Optimize(limit, null, _schema);

        result.Should().BeOfType<FullTextScanOp>()
            .Which.K.Should().Be(5);
    }

    [Fact]
    public void FullTextLimitPushdown_does_not_apply_when_skip_is_nonzero()
    {
        var ft = new FullTextScanOp(null, "ft_idx", "hello", 100);
        var limit = new LimitOp(ft, 5, 3);

        var result = LogicalOptimizer.Optimize(limit, null, _schema);

        result.Should().BeOfType<LimitOp>();
    }

    // ── KnnPushdown threshold edge cases ────────────────────────────────────────

    [Fact]
    public void KnnPushdown_with_fast_label_index_uses_dim_aware_threshold()
    {
        var labelId = _schema.GetOrCreateLabel("Medium");
        // dim=512 threshold=0.30 with fast index. 35% >= 0.30 => vector-first
        var stats = GraphStats.ForTest(1000,
            new Dictionary<LabelId, long> { [labelId] = 350 },
            hasFastLabelIndex: true);

        var knn = new KnnOp(null, "idx", new float[] { 1 }, 5, 512);
        var filter = new FilterOp(knn, s => new LabelPredicate(s.GetOrCreateLabel("Medium")));

        var result = LogicalOptimizer.Optimize(filter, stats, _schema);

        // 35% >= 30% (dim=512) => vector-first
        result.Should().BeOfType<FilterOp>();
    }

    [Fact]
    public void KnnPushdown_with_fast_label_index_high_dim_allows_higher_fraction()
    {
        var labelId = _schema.GetOrCreateLabel("HiDim");
        // dim=2048 threshold=0.70 with fast index. 60% < 0.70 => graph-first
        var stats = GraphStats.ForTest(1000,
            new Dictionary<LabelId, long> { [labelId] = 600 },
            hasFastLabelIndex: true);

        var knn = new KnnOp(null, "idx", new float[] { 1 }, 5, 2048);
        var filter = new FilterOp(knn, s => new LabelPredicate(s.GetOrCreateLabel("HiDim")));

        var result = LogicalOptimizer.Optimize(filter, stats, _schema);

        // 60% < 70% (dim=2048) => graph-first
        result.Should().BeOfType<KnnOp>()
            .Which.Candidate.Should().NotBeNull();
    }

    // ── FastIndexThresholdForDim unit tests ──────────────────────────────────────

    [Theory]
    [InlineData(128, 0.30)]
    [InlineData(512, 0.30)]
    [InlineData(768, 0.47)]
    [InlineData(1024, 0.47)]
    [InlineData(1536, 0.70)]
    [InlineData(2048, 0.70)]
    [InlineData(4096, 0.80)]
    public void FastIndexThresholdForDim_returns_expected_value(int dim, double expected)
    {
        LogicalOptimizer.FastIndexThresholdForDim(dim).Should().Be(expected);
    }

    [Fact]
    public void FastIndexThresholdForDim_zero_or_negative_returns_max_bucket()
    {
        LogicalOptimizer.FastIndexThresholdForDim(0).Should().Be(0.80);
        LogicalOptimizer.FastIndexThresholdForDim(-1).Should().Be(0.80);
    }

    // ── Composite: KnnPushdown + LabelScanRewrite ───────────────────────────────

    [Fact]
    public void KnnPushdown_graph_first_rewrites_label_filter_into_label_scan()
    {
        var knn = new KnnOp(null, "idx", new float[] { 1 }, 5, 128);
        var filter = new FilterOp(knn, s => new LabelPredicate(s.GetOrCreateLabel("Target")));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        var knnResult = result.Should().BeOfType<KnnOp>().Subject;
        var candidate = knnResult.Candidate.Should().BeOfType<ScanOp>().Subject;
        candidate.Kind.Should().Be(EntityKind.Vertex);
        candidate.Label.Should().NotBeNull();
    }

    // ── Composite: FullTextPushdown + LabelScanRewrite ──────────────────────────

    [Fact]
    public void FullTextPushdown_graph_first_rewrites_label_filter_into_label_scan()
    {
        var ft = new FullTextScanOp(null, "ft_idx", "query", 10);
        var filter = new FilterOp(ft, s => new LabelPredicate(s.GetOrCreateLabel("Page")));

        var result = LogicalOptimizer.Optimize(filter, null, _schema);

        var ftResult = result.Should().BeOfType<FullTextScanOp>().Subject;
        var candidate = ftResult.Candidate.Should().BeOfType<ScanOp>().Subject;
        candidate.Kind.Should().Be(EntityKind.Vertex);
        candidate.Label.Should().NotBeNull();
    }

    // ── Recursive child rewrite ─────────────────────────────────────────────────

    [Fact]
    public void LabelScanRewrite_applies_recursively_to_nested_children()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var filter = new FilterOp(scan, s => new LabelPredicate(s.GetOrCreateLabel("Inner")));
        var expand = new ExpandOp(filter, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly, null);

        var result = LogicalOptimizer.Optimize(expand, null, _schema);

        var expandResult = result.Should().BeOfType<ExpandOp>().Subject;
        expandResult.Source.Should().BeOfType<ScanOp>()
            .Which.Label.Should().NotBeNull();
    }

    [Fact]
    public void Optimizer_rewrites_children_inside_nexus_expansions_without_losing_shape()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var filter = new FilterOp(scan, s => new LabelPredicate(s.GetOrCreateLabel("Inner")));
        var toNexus = new ExpandToNexusOp(filter, 0, null, null, [0]);
        var members = new ExpandMembersOp(toNexus, 1, null, 0, [1]);

        var result = LogicalOptimizer.Optimize(members, null, _schema);

        var memberResult = result.Should().BeOfType<ExpandMembersOp>().Subject;
        memberResult.CurrentEntityColumn.Should().Be(1);
        memberResult.PredictedOutputColumnCount.Should().Be(3);
        var nexusResult = memberResult.Source.Should().BeOfType<ExpandToNexusOp>().Subject;
        nexusResult.CurrentEntityColumn.Should().Be(1);
        nexusResult.PredictedOutputColumnCount.Should().Be(3);
        nexusResult.Source.Should().BeOfType<ScanOp>()
            .Which.Label.Should().NotBeNull();
    }

    // ── SIG: ApplyDyadicOp child rewrite ────────────────────────────────────────

    [Fact]
    public void Optimizer_rewrites_children_inside_ApplyDyadicOp()
    {
        DyadicScoreFunc scorer = (a, b, r) => 1.0f;
        var scan = new ScanOp(EntityKind.Vertex, null);
        var labelFilter = new FilterOp(scan, s => new LabelPredicate(s.GetOrCreateLabel("Signal")));
        var ad = new ApplyDyadicOp(
            labelFilter, typeof(IDyadicOperator<float>), "emb", "vec_idx",
            new float[] { 1 }, null, null, 5, scorer);

        var result = LogicalOptimizer.Optimize(ad, null, _schema);

        var adResult = result.Should().BeOfType<ApplyDyadicOp>().Subject;
        adResult.Source.Should().BeOfType<ScanOp>()
            .Which.Label.Should().NotBeNull();
    }

    [Fact]
    public void Optimizer_rewrites_BPlan_inside_ApplyDyadicOp()
    {
        DyadicScoreFunc scorer = (a, b, r) => 1.0f;
        var source = new ScanOp(EntityKind.Vertex, new LabelId(1));
        var bScan = new ScanOp(EntityKind.Vertex, null);
        var bFilter = new FilterOp(bScan, s => new LabelPredicate(s.GetOrCreateLabel("Ref")));
        var ad = new ApplyDyadicOp(
            source, typeof(IDyadicOperator<float>), "emb", "vec_idx",
            null, bFilter, null, 5, scorer);

        var result = LogicalOptimizer.Optimize(ad, null, _schema);

        var adResult = result.Should().BeOfType<ApplyDyadicOp>().Subject;
        adResult.BPlan.Should().BeOfType<ScanOp>()
            .Which.Label.Should().NotBeNull();
    }

    // ── Limit + Filter + Knn combined ───────────────────────────────────────────

    [Fact]
    public void KnnLimitPushdown_through_filters_shrinks_K_then_pushes_down()
    {
        var knn = new KnnOp(null, "idx", new float[] { 1 }, 50, 128);
        var filter = new FilterOp(knn, s => new LabelPredicate(s.GetOrCreateLabel("X")));
        var limit = new LimitOp(filter, 3, 0);

        var result = LogicalOptimizer.Optimize(limit, null, _schema);

        var knnResult = result.Should().BeOfType<KnnOp>().Subject;
        knnResult.K.Should().Be(3);
        knnResult.Candidate.Should().NotBeNull();
    }
}

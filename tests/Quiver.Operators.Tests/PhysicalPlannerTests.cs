using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public sealed class PhysicalPlannerTests
{
    private readonly StubSchemaApi _schema = new();

    // ── ScanOp ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ScanOp_node_no_label_produces_AllNodesScanOperator()
    {
        var plan = new ScanOp(EntityKind.Node, null);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<AllNodesScanOperator>();
    }

    [Fact]
    public void ScanOp_node_with_label_produces_NodeByLabelScanOperator()
    {
        var plan = new ScanOp(EntityKind.Node, new LabelId(1));
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<NodeByLabelScanOperator>();
    }

    [Fact]
    public void ScanOp_relationship_produces_AllRelationshipsScanOperator()
    {
        var plan = new ScanOp(EntityKind.Relationship, null);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<AllRelationshipsScanOperator>();
    }

    [Fact]
    public void ScanOp_hyperedge_produces_AllHyperedgesScanOperator()
    {
        var plan = new ScanOp(EntityKind.Hyperedge, null);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<AllHyperedgesScanOperator>();
    }

    // ── NodeSeedOp ──────────────────────────────────────────────────────────────

    [Fact]
    public void NodeSeedOp_single_id_produces_SingleNodeOperator()
    {
        var plan = new NodeSeedOp([new NodeId(42)]);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<SingleNodeOperator>();
    }

    [Fact]
    public void NodeSeedOp_multiple_ids_produces_MultiNodeOperator()
    {
        var plan = new NodeSeedOp([new NodeId(1), new NodeId(2)]);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<MultiNodeOperator>();
    }

    // ── FilterOp ────────────────────────────────────────────────────────────────

    [Fact]
    public void FilterOp_wraps_source_in_FilterOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var filter = new FilterOp(scan, _ => new AlwaysTruePredicate());
        var op = PhysicalPlanner.Plan(filter, _schema);
        op.Should().BeOfType<FilterOperator>();
    }

    // ── ExpandOp ────────────────────────────────────────────────────────────────

    [Fact]
    public void ExpandOp_produces_ExpandOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var expand = new ExpandOp(scan, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly, null);
        var op = PhysicalPlanner.Plan(expand, _schema);
        op.Should().BeOfType<ExpandOperator>();
    }

    [Fact]
    public void ExpandOp_with_type_filter_produces_ExpandOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var expand = new ExpandOp(scan, 0, Direction.Outgoing, "KNOWS", ExpandOutputMode.Full, null);
        var op = PhysicalPlanner.Plan(expand, _schema);
        op.Should().BeOfType<ExpandOperator>();
    }

    [Fact]
    public void ExpandToHyperedgeOp_resolves_filters_and_preserves_shape()
    {
        _schema.GetOrCreateHyperedgeType("Fact");
        _schema.GetOrCreateRole("Subject");
        var scan = new ScanOp(EntityKind.Node, null);
        var expand = new ExpandToHyperedgeOp(scan, 0, "Fact", "Subject", [0]);

        var op = PhysicalPlanner.Plan(expand, _schema);

        op.Should().BeOfType<ExpandToHyperedgeOperator>();
        expand.CurrentEntityColumn.Should().Be(1);
        expand.PredictedOutputColumnCount.Should().Be(3);
    }

    [Fact]
    public void ExpandMembersOp_resolves_role_and_preserves_shape()
    {
        _schema.GetOrCreateRole("Object");
        var scan = new ScanOp(EntityKind.Hyperedge, null);
        var expand = new ExpandMembersOp(scan, 0, "Object", null, [0]);

        var op = PhysicalPlanner.Plan(expand, _schema);

        op.Should().BeOfType<ExpandMembersOperator>();
        expand.CurrentEntityColumn.Should().Be(1);
        expand.PredictedOutputColumnCount.Should().Be(3);
    }

    // ── KnnOp ───────────────────────────────────────────────────────────────────

    [Fact]
    public void KnnOp_without_candidate_produces_KnnNodeSourceOperator()
    {
        var knn = new KnnOp(null, "vec_idx", new float[] { 1, 2, 3 }, 5, 3);
        var op = PhysicalPlanner.Plan(knn, _schema);
        op.Should().BeOfType<KnnNodeSourceOperator>();
    }

    [Fact]
    public void KnnOp_with_candidate_produces_FilteredKnnNodeSourceOperator()
    {
        var scan = new ScanOp(EntityKind.Node, new LabelId(1));
        var knn = new KnnOp(scan, "vec_idx", new float[] { 1, 2, 3 }, 5, 3);
        var op = PhysicalPlanner.Plan(knn, _schema);
        op.Should().BeOfType<FilteredKnnNodeSourceOperator>();
    }

    // ── FullTextScanOp ──────────────────────────────────────────────────────────

    [Fact]
    public void FullTextScanOp_without_candidate_produces_FullTextScanOperator()
    {
        var ft = new FullTextScanOp(null, "ft_idx", "hello", 10);
        var op = PhysicalPlanner.Plan(ft, _schema);
        op.Should().BeOfType<FullTextScanOperator>();
    }

    [Fact]
    public void FullTextScanOp_with_candidate_produces_FilteredFullTextScanOperator()
    {
        var scan = new ScanOp(EntityKind.Node, new LabelId(1));
        var ft = new FullTextScanOp(scan, "ft_idx", "hello", 10);
        var op = PhysicalPlanner.Plan(ft, _schema);
        op.Should().BeOfType<FilteredFullTextScanOperator>();
    }

    // ── FusionOp ────────────────────────────────────────────────────────────────

    [Fact]
    public void FusionOp_produces_FusionOperator()
    {
        var knn = new KnnOp(null, "v", new float[] { 1 }, 5, 1);
        var ft = new FullTextScanOp(null, "ft", "q", 5);
        var fusion = new FusionOp([knn, ft], 5, FusionStrategy.Rrf);
        var op = PhysicalPlanner.Plan(fusion, _schema);
        op.Should().BeOfType<FusionOperator>();
    }

    // ── LimitOp ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LimitOp_produces_LimitOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var limit = new LimitOp(scan, 10, 0);
        var op = PhysicalPlanner.Plan(limit, _schema);
        op.Should().BeOfType<LimitOperator>();
    }

    // ── SortOp ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SortOp_with_property_key_produces_SortOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var sort = new SortOp(scan, "age", 1, Descending: true);
        var op = PhysicalPlanner.Plan(sort, _schema);
        op.Should().BeOfType<SortOperator>();
    }

    [Fact]
    public void SortOp_without_property_key_produces_SortOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var sort = new SortOp(scan, null, 0, Descending: false);
        var op = PhysicalPlanner.Plan(sort, _schema);
        op.Should().BeOfType<SortOperator>();
    }

    // ── DedupOp ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DedupOp_produces_PathDedupOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var dedup = new DedupOp(scan, 0);
        var op = PhysicalPlanner.Plan(dedup, _schema);
        op.Should().BeOfType<PathDedupOperator>();
    }

    // ── PropertyLookupOp ────────────────────────────────────────────────────────

    [Fact]
    public void PropertyLookupOp_produces_PropertyLookupOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var lookup = new PropertyLookupOp(scan, "name", EntityKind.Node);
        var op = PhysicalPlanner.Plan(lookup, _schema);
        op.Should().BeOfType<PropertyLookupOperator>();
    }

    // ── SIG: ApplyDyadicOp plan generation ──────────────────────────────────────

    [Fact]
    public void ApplyDyadicOp_produces_ApplyDyadicOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        DyadicScoreFunc scorer = (a, b, r) => 1.0f;
        var ad = new ApplyDyadicOp(
            scan, typeof(IDyadicOperator<float>), "emb", "vec_idx",
            new float[] { 1, 2, 3 }, null, null, 5, scorer);

        var op = PhysicalPlanner.Plan(ad, _schema);
        op.Should().BeOfType<ApplyDyadicOperator>();
    }

    [Fact]
    public void ApplyDyadicOp_with_oversample_produces_ApplyDyadicOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        DyadicScoreFunc scorer = (a, b, r) => 1.0f;
        var ad = new ApplyDyadicOp(
            scan, typeof(IDyadicOperator<float>), "emb", "vec_idx",
            new float[] { 1, 2, 3 }, null, null, 5, scorer, Oversample: 4);

        var op = PhysicalPlanner.Plan(ad, _schema);
        op.Should().BeOfType<ApplyDyadicOperator>();
    }

    [Fact]
    public void ApplyDyadicOp_with_BPlan_produces_ApplyDyadicOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var bPlan = new ScanOp(EntityKind.Node, null);
        DyadicScoreFunc scorer = (a, b, r) => 1.0f;
        var ad = new ApplyDyadicOp(
            scan, typeof(IDyadicOperator<float>), "emb", "vec_idx",
            null, bPlan, null, 5, scorer);

        var op = PhysicalPlanner.Plan(ad, _schema);
        op.Should().BeOfType<ApplyDyadicOperator>();
    }

    [Fact]
    public void ApplyDyadicOp_with_regions_produces_ApplyDyadicOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        DyadicScoreFunc scorer = (a, b, r) => 1.0f;
        var regions = new Range[] { 0..10, 20..30 };
        var ad = new ApplyDyadicOp(
            scan, typeof(IDyadicOperator<float>), "emb", "vec_idx",
            new float[] { 1, 2, 3 }, null, regions, 5, scorer);

        var op = PhysicalPlanner.Plan(ad, _schema);
        op.Should().BeOfType<ApplyDyadicOperator>();
    }

    // ── VarLenExpandOp ──────────────────────────────────────────────────────────

    [Fact]
    public void VarLenExpandOp_produces_VariableLengthExpandOperator()
    {
        var scan = new ScanOp(EntityKind.Node, null);
        var varLen = new VarLenExpandOp(scan, Direction.Outgoing, null, 1, 3);
        var op = PhysicalPlanner.Plan(varLen, _schema);
        op.Should().BeOfType<VariableLengthExpandOperator>();
    }

    // ── Unsupported op ──────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_LogicalOp_throws_NotSupportedException()
    {
        var unknown = new UnknownTestOp();
        var act = () => PhysicalPlanner.Plan(unknown, _schema);
        act.Should().Throw<NotSupportedException>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private sealed record UnknownTestOp() : LogicalOp
    {
        public override int CurrentEntityColumn => 0;
        public override int PredictedOutputColumnCount => 1;
    }
}

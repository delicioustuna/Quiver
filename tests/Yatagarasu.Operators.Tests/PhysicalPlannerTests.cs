using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Query.Logical;
using Yatagarasu.Query.Optimizer;
using Yatagarasu.Query.Physical;
using Yatagarasu.Query.Physical.Tests.Support;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public sealed class PhysicalPlannerTests
{
    private readonly StubSchemaApi _schema = new();

    // ── ScanOp ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ScanOp_vertex_no_label_produces_AllVerticesScanOperator()
    {
        var plan = new ScanOp(EntityKind.Vertex, null);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<AllVerticesScanOperator>();
    }

    [Fact]
    public void ScanOp_vertex_with_label_produces_VertexByLabelScanOperator()
    {
        var plan = new ScanOp(EntityKind.Vertex, new LabelId(1));
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<VertexByLabelScanOperator>();
    }

    [Fact]
    public void ScanOp_edge_produces_AllEdgesScanOperator()
    {
        var plan = new ScanOp(EntityKind.Edge, null);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<AllEdgesScanOperator>();
    }

    [Fact]
    public void ScanOp_nexus_produces_AllNexusesScanOperator()
    {
        var plan = new ScanOp(EntityKind.Nexus, null);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<AllNexusesScanOperator>();
    }

    // ── VertexSeedOp ──────────────────────────────────────────────────────────────

    [Fact]
    public void VertexSeedOp_single_id_produces_SingleVertexOperator()
    {
        var plan = new VertexSeedOp([new VertexId(42)]);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<SingleVertexOperator>();
    }

    [Fact]
    public void VertexSeedOp_multiple_ids_produces_MultiVertexOperator()
    {
        var plan = new VertexSeedOp([new VertexId(1), new VertexId(2)]);
        var op = PhysicalPlanner.Plan(plan, _schema);
        op.Should().BeOfType<MultiVertexOperator>();
    }

    // ── FilterOp ────────────────────────────────────────────────────────────────

    [Fact]
    public void FilterOp_wraps_source_in_FilterOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var filter = new FilterOp(scan, _ => new AlwaysTruePredicate());
        var op = PhysicalPlanner.Plan(filter, _schema);
        op.Should().BeOfType<FilterOperator>();
    }

    // ── ExpandOp ────────────────────────────────────────────────────────────────

    [Fact]
    public void ExpandOp_produces_ExpandOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var expand = new ExpandOp(scan, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly, null);
        var op = PhysicalPlanner.Plan(expand, _schema);
        op.Should().BeOfType<ExpandOperator>();
    }

    [Fact]
    public void ExpandOp_with_type_filter_produces_ExpandOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var expand = new ExpandOp(scan, 0, Direction.Outgoing, "KNOWS", ExpandOutputMode.Full, null);
        var op = PhysicalPlanner.Plan(expand, _schema);
        op.Should().BeOfType<ExpandOperator>();
    }

    [Fact]
    public void ExpandToNexusOp_resolves_filters_and_preserves_shape()
    {
        _schema.GetOrCreateNexusType("Fact");
        _schema.GetOrCreateRole("Subject");
        var scan = new ScanOp(EntityKind.Vertex, null);
        var expand = new ExpandToNexusOp(scan, 0, "Fact", "Subject", [0]);

        var op = PhysicalPlanner.Plan(expand, _schema);

        op.Should().BeOfType<ExpandToNexusOperator>();
        expand.CurrentEntityColumn.Should().Be(1);
        expand.PredictedOutputColumnCount.Should().Be(3);
    }

    [Fact]
    public void ExpandMembersOp_resolves_role_and_preserves_shape()
    {
        _schema.GetOrCreateRole("Object");
        var scan = new ScanOp(EntityKind.Nexus, null);
        var expand = new ExpandMembersOp(scan, 0, "Object", null, [0]);

        var op = PhysicalPlanner.Plan(expand, _schema);

        op.Should().BeOfType<ExpandMembersOperator>();
        expand.CurrentEntityColumn.Should().Be(1);
        expand.PredictedOutputColumnCount.Should().Be(3);
    }

    // ── KnnOp ───────────────────────────────────────────────────────────────────

    [Fact]
    public void KnnOp_without_candidate_produces_KnnVertexSourceOperator()
    {
        var knn = new KnnOp(null, "vec_idx", new float[] { 1, 2, 3 }, 5, 3);
        var op = PhysicalPlanner.Plan(knn, _schema);
        op.Should().BeOfType<KnnVertexSourceOperator>();
    }

    [Fact]
    public void KnnOp_with_candidate_produces_FilteredKnnVertexSourceOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, new LabelId(1));
        var knn = new KnnOp(scan, "vec_idx", new float[] { 1, 2, 3 }, 5, 3);
        var op = PhysicalPlanner.Plan(knn, _schema);
        op.Should().BeOfType<FilteredKnnVertexSourceOperator>();
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
        var scan = new ScanOp(EntityKind.Vertex, new LabelId(1));
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
        var scan = new ScanOp(EntityKind.Vertex, null);
        var limit = new LimitOp(scan, 10, 0);
        var op = PhysicalPlanner.Plan(limit, _schema);
        op.Should().BeOfType<LimitOperator>();
    }

    // ── SortOp ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SortOp_with_property_key_produces_SortOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var sort = new SortOp(scan, "age", 1, Descending: true);
        var op = PhysicalPlanner.Plan(sort, _schema);
        op.Should().BeOfType<SortOperator>();
    }

    [Fact]
    public void SortOp_without_property_key_produces_SortOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var sort = new SortOp(scan, null, 0, Descending: false);
        var op = PhysicalPlanner.Plan(sort, _schema);
        op.Should().BeOfType<SortOperator>();
    }

    // ── DedupOp ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DedupOp_produces_PathDedupOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var dedup = new DedupOp(scan, 0);
        var op = PhysicalPlanner.Plan(dedup, _schema);
        op.Should().BeOfType<PathDedupOperator>();
    }

    // ── PropertyLookupOp ────────────────────────────────────────────────────────

    [Fact]
    public void PropertyLookupOp_produces_PropertyLookupOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
        var lookup = new PropertyLookupOp(scan, "name", EntityKind.Vertex);
        var op = PhysicalPlanner.Plan(lookup, _schema);
        op.Should().BeOfType<PropertyLookupOperator>();
    }

    // ── SIG: ApplyDyadicOp plan generation ──────────────────────────────────────

    [Fact]
    public void ApplyDyadicOp_produces_ApplyDyadicOperator()
    {
        var scan = new ScanOp(EntityKind.Vertex, null);
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
        var scan = new ScanOp(EntityKind.Vertex, null);
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
        var scan = new ScanOp(EntityKind.Vertex, null);
        var bPlan = new ScanOp(EntityKind.Vertex, null);
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
        var scan = new ScanOp(EntityKind.Vertex, null);
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
        var scan = new ScanOp(EntityKind.Vertex, null);
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

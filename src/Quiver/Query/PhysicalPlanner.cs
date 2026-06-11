using Quiver;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Query.Optimizer;

/// <summary>
/// ARCH-7: 論理プラン (<see cref="LogicalOp"/>) を物理オペレータ (<see cref="IPhysicalOperator"/>) へ
/// 落とす physical planner。旧 <c>IOperatorBuilder.Build</c> 群の本体を 1 箇所の選択表に集約したもの。
/// 実行エンジンは現行 pull 型を踏襲し、本クラスは「どの物理オペレータを選ぶか」だけを担う。
/// </summary>
internal static class PhysicalPlanner
{
    /// <summary>論理プランツリーを物理オペレータツリーへ変換する。</summary>
    public static IPhysicalOperator Plan(LogicalOp op, ISchemaApi schema) => op switch
    {
        ScanOp s                  => PlanScan(s, schema),
        NodeSeedOp n              => n.Ids.Length == 1
                                        ? new SingleNodeOperator(n.Ids[0])
                                        : new MultiNodeOperator(n.Ids),
        CorrelatedInputOp c       => c.Probe,
        FilterOp f                => new FilterOperator(Plan(f.Source, schema), f.PredicateFactory(schema)),
        ExpandOp e                => PlanExpand(e, schema),
        VarLenExpandOp v          => PlanVarLenExpand(v, schema),
        PathOp p                  => PlanPath(p, schema),
        KnnOp k                   => PlanKnn(k, schema),
        FullTextScanOp ft         => PlanFullTextScan(ft, schema),
        FusionOp fu               => PlanFusion(fu, schema),
        PropertyLookupOp pl       => new PropertyLookupOperator(
                                        Plan(pl.Source, schema), pl.Source.CurrentEntityColumn,
                                        schema.GetOrCreatePropertyKey(pl.Key), pl.Key,
                                        PropertyTypeFlags.Scalar, pl.Kind),
        LabelNameLookupOp ln      => new LabelNameLookupOperator(
                                        Plan(ln.Source, schema), ln.NodeColumn, schema.GetLabelName),
        RelationshipEndpointOp re => new RelationshipEndpointOperator(
                                        Plan(re.Source, schema), re.RelColumn, re.Endpoint),
        LimitOp l                 => new LimitOperator(Plan(l.Source, schema), l.Limit, l.Skip),
        SortOp so                 => PlanSort(so, schema),
        DedupOp d                 => new PathDedupOperator(Plan(d.Source, schema), d.KeyColumn),
        BranchOp b                => PlanBranch(b, schema),
        _ => throw new NotSupportedException($"未対応の LogicalOp: {op.GetType().Name}"),
    };

    private static IPhysicalOperator PlanScan(ScanOp s, ISchemaApi schema)
    {
        if (s.Kind == EntityKind.Relationship)
            return new AllRelationshipsScanOperator();
        if (s.Label is LabelId lid)
            return new NodeByLabelScanOperator(lid);
        return new AllNodesScanOperator();
    }

    private static IPhysicalOperator PlanExpand(ExpandOp e, ISchemaApi schema)
    {
        RelationshipTypeId? typeId = e.Type != null ? schema.GetOrCreateRelationshipType(e.Type) : null;
        return new ExpandOperator(Plan(e.Source, schema), e.SourceColumn, e.Direction, typeId, e.Mode, e.Carry);
    }

    private static IPhysicalOperator PlanVarLenExpand(VarLenExpandOp v, ISchemaApi schema)
    {
        RelationshipTypeId? typeId = v.Type != null ? schema.GetOrCreateRelationshipType(v.Type) : null;
        return new VariableLengthExpandOperator(
            Plan(v.Source, schema), v.Source.CurrentEntityColumn, v.Direction, typeId, v.MinHops, v.MaxHops);
    }

    private static IPhysicalOperator PlanPath(PathOp p, ISchemaApi schema)
    {
        RelationshipTypeId? typeId = p.Type != null ? schema.GetOrCreateRelationshipType(p.Type) : null;
        var pair = new PairWithConstantOperator(Plan(p.Source, schema), p.Source.CurrentEntityColumn, p.Target);
        return new ShortestPathOperator(pair, 0, 1, p.Direction, typeId, p.MaxDistance);
    }

    private static IPhysicalOperator PlanKnn(KnnOp k, ISchemaApi schema)
    {
        if (k.Candidate is null)
            return new KnnNodeSourceOperator(k.IndexName, k.Query, k.K);
        return new FilteredKnnNodeSourceOperator(
            Plan(k.Candidate, schema), k.Candidate.CurrentEntityColumn, k.IndexName, k.Query, k.K);
    }

    private static IPhysicalOperator PlanFullTextScan(FullTextScanOp ft, ISchemaApi schema)
    {
        // FTS-3: text-first のみ。graph-first (Candidate!=null + .FilterByText / pushdown) は FTS-4。
        if (ft.Candidate is not null)
            throw new NotSupportedException("Graph-first full-text scan (candidate-side) lands in FTS-4.");
        return new FullTextScanOperator(ft.IndexName, ft.QueryText, ft.K);
    }

    private static IPhysicalOperator PlanFusion(FusionOp fu, ISchemaApi schema)
    {
        // FTS-5: Phase 1 は RRF のみ。enum 拡張時に他戦略の物理化をここへ足す。
        if (fu.Strategy != FusionStrategy.Rrf)
            throw new NotSupportedException($"Fusion strategy {fu.Strategy} は未対応 (Phase 1 は RRF のみ)。");

        var children = new IPhysicalOperator[fu.Children.Length];
        var columns = new int[fu.Children.Length];
        for (int i = 0; i < fu.Children.Length; i++)
        {
            children[i] = Plan(fu.Children[i], schema);
            columns[i] = fu.Children[i].CurrentEntityColumn;
        }
        return new FusionOperator(children, columns, fu.K);
    }

    private static IPhysicalOperator PlanSort(SortOp so, ISchemaApi schema)
    {
        if (so.PropertyKey != null)
        {
            var keyId = schema.GetOrCreatePropertyKey(so.PropertyKey);
            var withProp = new PropertyLookupOperator(
                Plan(so.Source, schema), so.Source.CurrentEntityColumn, keyId, so.PropertyKey);
            return new SortOperator(withProp, so.SortColumn, so.Descending);
        }
        return new SortOperator(Plan(so.Source, schema), so.SortColumn, so.Descending);
    }

    private static IPhysicalOperator PlanBranch(BranchOp b, ISchemaApi schema)
    {
        var (probes, branches) = b.BuildBranches(schema);
        var src = Plan(b.Source, schema);
        int srcCol = b.Source.CurrentEntityColumn;
        return b.Kind switch
        {
            LogicalBranchKind.Union    => new UnionOperator(src, srcCol, probes, branches),
            LogicalBranchKind.Coalesce => new CoalesceOperator(src, srcCol, probes, branches),
            LogicalBranchKind.Optional => new OptionalOperator(src, srcCol, probes[0], branches[0]),
            _ => throw new InvalidOperationException(),
        };
    }
}

using Quiver;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Query.Optimizer;

/// <summary>
/// 論理プラン (<see cref="LogicalOp"/>) を物理オペレータ (<see cref="IPhysicalOperator"/>) へ
/// 落とす physical planner。旧 <c>IOperatorBuilder.Build</c> 群の本体を 1 箇所の選択表に集約したもの。
/// 実行エンジンは現行 pull 型を踏襲し、本クラスは「どの物理オペレータを選ぶか」だけを担う。
/// </summary>
internal static class PhysicalPlanner
{
    /// <summary>論理プランツリーを物理オペレータツリーへ変換する。</summary>
    public static IPhysicalOperator Plan(LogicalOp op, ISchemaApi schema) => op switch
    {
        ScanOp s                  => PlanScan(s, schema),
        VertexSeedOp n              => n.Ids.Length == 1
                                        ? new SingleVertexOperator(n.Ids[0])
                                        : new MultiVertexOperator(n.Ids),
        NexusSeedOp h         => new SingleNexusOperator(h.Id),
        CorrelatedInputOp c       => c.Probe,
        FilterOp f                => new FilterOperator(Plan(f.Source, schema), f.PredicateFactory(schema)),
        ExpandOp e                => PlanExpand(e, schema),
        ExpandToNexusOp eh    => PlanExpandToNexus(eh, schema),
        ExpandMembersOp em        => PlanExpandMembers(em, schema),
        VarLenExpandOp v          => PlanVarLenExpand(v, schema),
        PathOp p                  => PlanPath(p, schema),
        KnnOp k                   => PlanKnn(k, schema),
        FullTextScanOp ft         => PlanFullTextScan(ft, schema),
        FusionOp fu               => PlanFusion(fu, schema),
        ApplyDyadicOp ad          => PlanApplyDyadic(ad, schema),
        PropertyLookupOp pl       => new PropertyLookupOperator(
                                        Plan(pl.Source, schema), pl.Source.CurrentEntityColumn,
                                        schema.GetOrCreatePropertyKey(pl.Key), pl.Key,
                                        PropertyTypeFlags.Scalar | PropertyTypeFlags.FloatArray, pl.Kind),
        LabelNameLookupOp ln      => new LabelNameLookupOperator(
                                        Plan(ln.Source, schema), ln.VertexColumn, schema.GetLabelName),
        EdgeEndpointOp re => new EdgeEndpointOperator(
                                        Plan(re.Source, schema), re.EdgeColumn, re.Endpoint),
        LimitOp l                 => new LimitOperator(Plan(l.Source, schema), l.Limit, l.Skip),
        SortOp so                 => PlanSort(so, schema),
        DedupOp d                 => new PathDedupOperator(Plan(d.Source, schema), d.KeyColumn),
        BranchOp b                => PlanBranch(b, schema),
        _ => throw new NotSupportedException($"未対応の LogicalOp: {op.GetType().Name}"),
    };

    private static IPhysicalOperator PlanScan(ScanOp s, ISchemaApi schema)
    {
        if (s.Kind == EntityKind.Nexus)
            return new AllNexusesScanOperator();
        if (s.Kind == EntityKind.Edge)
            return new AllEdgesScanOperator();
        if (s.Label is LabelId lid)
            return new VertexByLabelScanOperator(lid);
        return new AllVerticesScanOperator();
    }

    private static IPhysicalOperator PlanExpand(ExpandOp e, ISchemaApi schema)
    {
        EdgeTypeId? typeId = e.Type != null ? schema.GetOrCreateEdgeType(e.Type) : null;
        return new ExpandOperator(Plan(e.Source, schema), e.SourceColumn, e.Direction, typeId, e.Mode, e.Carry);
    }

    private static IPhysicalOperator PlanExpandToNexus(
        ExpandToNexusOp e,
        ISchemaApi schema)
        => new ExpandToNexusOperator(
            Plan(e.Source, schema),
            e.SourceVertexColumn,
            ResolveNexusType(e.Type, schema),
            ResolveRole(e.Role, schema),
            e.Carry);

    private static IPhysicalOperator PlanExpandMembers(
        ExpandMembersOp e,
        ISchemaApi schema)
    {
        var fallback = new ExpandMembersOperator(
            Plan(e.Source, schema),
            e.NexusColumn,
            ResolveRole(e.Role, schema),
            e.ExcludeVertexColumn,
            e.Carry);

        // 起点ロールと取得ロールが共に明示された OtherMembers だけが物理ビューの
        // 一意なキーになる。片方でも未指定なら通常の incidence 展開を維持する。
        if (e.Source is not ExpandToNexusOp origin
            || e.ExcludeVertexColumn is null
            || origin.Role is null
            || e.Role is null)
            return fallback;

        RoleId? originRole = ResolveRole(origin.Role, schema);
        RoleId? memberRole = ResolveRole(e.Role, schema);
        if (!originRole.HasValue || !originRole.Value.IsValid
            || !memberRole.HasValue || !memberRole.Value.IsValid)
            return fallback;

        return new CoMembershipOperator(
            Plan(origin.Source, schema),
            fallback,
            origin.SourceVertexColumn,
            ResolveNexusType(origin.Type, schema),
            originRole.Value,
            memberRole.Value,
            origin.Carry,
            e.Carry);
    }

    private static NexusTypeId? ResolveNexusType(string? name, ISchemaApi schema)
    {
        if (name == null) return null;
        return schema.TryGetNexusTypeId(name, out var id)
            ? id
            : NexusTypeId.Invalid;
    }

    private static RoleId? ResolveRole(string? name, ISchemaApi schema)
    {
        if (name == null) return null;
        return schema is INexusSchemaResolver resolver
            && resolver.TryGetRoleId(name, out var id)
                ? id
                : RoleId.Invalid;
    }

    private static IPhysicalOperator PlanVarLenExpand(VarLenExpandOp v, ISchemaApi schema)
    {
        EdgeTypeId? typeId = v.Type != null ? schema.GetOrCreateEdgeType(v.Type) : null;
        return new VariableLengthExpandOperator(
            Plan(v.Source, schema), v.Source.CurrentEntityColumn, v.Direction, typeId, v.MinHops, v.MaxHops);
    }

    private static IPhysicalOperator PlanPath(PathOp p, ISchemaApi schema)
    {
        EdgeTypeId? typeId = p.Type != null ? schema.GetOrCreateEdgeType(p.Type) : null;
        var pair = new PairWithConstantOperator(Plan(p.Source, schema), p.Source.CurrentEntityColumn, p.Target);
        return new ShortestPathOperator(pair, 0, 1, p.Direction, typeId, p.MaxDistance);
    }

    private static IPhysicalOperator PlanKnn(KnnOp k, ISchemaApi schema)
    {
        if (k.Candidate is null)
            return new KnnVertexSourceOperator(k.IndexName, k.Query, k.K, k.Options);
        return new FilteredKnnVertexSourceOperator(
            Plan(k.Candidate, schema), k.Candidate.CurrentEntityColumn,
            k.IndexName, k.Query, k.K, k.Options);
    }

    private static IPhysicalOperator PlanFullTextScan(FullTextScanOp ft, ISchemaApi schema)
    {
        // text-first (Candidate=null) / graph-first (Candidate!=null = 候補集合内 BM25)。
        if (ft.Candidate is null)
            return new FullTextScanOperator(ft.IndexName, ft.QueryText, ft.K, ft.Corpus);
        return new FilteredFullTextScanOperator(
            Plan(ft.Candidate, schema), ft.Candidate.CurrentEntityColumn,
            ft.IndexName, ft.QueryText, ft.K, ft.Corpus);
    }

    private static IPhysicalOperator PlanFusion(FusionOp fu, ISchemaApi schema)
    {
        // Phase 1 は RRF のみ。enum 拡張時に他戦略の物理化をここへ足す。
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

    private static IPhysicalOperator PlanApplyDyadic(ApplyDyadicOp ad, ISchemaApi schema)
    {
        IPhysicalOperator? bSource = null;
        int bFloatColumn = 0;
        if (ad.BPlan is not null)
        {
            bSource = Plan(ad.BPlan, schema);
            bFloatColumn = ad.BPlan.PredictedOutputColumnCount - 1;
        }

        return new ApplyDyadicOperator(
            Plan(ad.Source, schema),
            ad.Source.CurrentEntityColumn,
            ad.IndexName,
            ad.BVector,
            bSource,
            bFloatColumn,
            ad.Regions,
            ad.K,
            ad.Scorer,
            ad.OperatorType,
            ad.Oversample);
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

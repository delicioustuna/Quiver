using Yatagarasu;
using Yatagarasu.Api.Internal;
using Yatagarasu.Query.Physical;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// KNN ベンチの「vector-first (post-filter) baseline」を optimizer を介さず
/// 物理オペレータを直接構築して測るためのヘルパ。旧来は内部 <c>KnnVertexSourceBuilder</c> を
/// 直接構築して PendingKnn rewrite を bypass していたが、LogicalPlan 化で push-down が
/// optimizer に集約されたため、KNN top-K → label post-filter の物理プランを直に組んで測る。
/// </summary>
internal static class KnnBenchSupport
{
    /// <summary>vector-first: KNN top-K を全 N から取得後、label で post-filter した件数を返す。</summary>
    public static int PostFilterCount(
        IReadTransaction rtx, ISchemaCatalog schema, string indexName, float[] query, int k, string label)
    {
        if (!schema.TryGetLabelId(label, out var labelId))
            return 0;

        var knn = new KnnVertexSourceOperator(indexName, query, k);
        var filtered = new FilterOperator(knn, new LabelPredicate(labelId, 0));
        int count = 0;
        using var result = rtx.Execute(filtered);
        foreach (var _ in result.Rows()) count++;
        return count;
    }
}

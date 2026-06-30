using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api.Match;

internal static class MatchCompiler
{
    // (plan, 変数から列へのマップ) を返す。
    // 2 ノード 1 エッジでは Full expand → [source(0), rel(1), neighbor(2)]。
    internal static (IPhysicalOperator plan, Dictionary<string, int> varToColumn) Compile(
        IGraphTransaction tx,
        ISchemaApi schema,
        GraphPattern pattern,
        List<(string variable, string key, PropertyPredicate pred)> wherePredicates)
    {
        var varToColumn = new Dictionary<string, int>();

        LogicalOp builder;
        var startNode = pattern.StartNode;

        builder = new ScanOp(
            EntityKind.Node,
            startNode.Label != null ? schema.GetOrCreateLabel(startNode.Label) : null);

        if (pattern.Edge == null || pattern.EndNode == null)
        {
            // 単一ノードパターン。
            varToColumn[startNode.Variable] = 0;
        }
        else
        {
            // 2 ノード 1 エッジパターン。3 列すべてを残すため Full 出力を使う。
            var edge = pattern.Edge;
            var endNode = pattern.EndNode;
            var direction = edge.Outgoing ? Direction.Outgoing : Direction.Incoming;

            // Full expand の列配置は col0=source, col1=rel, col2=neighbor。
            builder = new ExpandOp(builder, builder.CurrentEntityColumn, direction, edge.Type, ExpandOutputMode.Full, null);
            varToColumn[startNode.Variable] = 0;
            varToColumn[endNode.Variable]   = 2;

            // 終端ノードをラベルで絞り込む。
            if (endNode.Label != null)
            {
                var labelId = schema.GetOrCreateLabel(endNode.Label);
                builder = new FilterOp(builder, _ => new LabelPredicate(labelId, column: 2));
            }
        }

        // WHERE 述語を適用する。
        foreach (var (variable, key, pred) in wherePredicates)
        {
            if (!varToColumn.TryGetValue(variable, out int entityCol))
                throw new InvalidOperationException($"Unknown variable '{variable}' in WHERE clause.");

            var keyId = schema.GetOrCreatePropertyKey(key);
            var capturedCol  = entityCol;
            var capturedKey  = keyId;
            var capturedPred = pred;

            if (pred.Kind == PredicateKind.Eq && pred.StringValue != null)
            {
                var strVal = pred.StringValue;
                builder = new FilterOp(builder, _ => new PropertyEqStringPredicate(capturedCol, capturedKey, strVal));
            }
            else if (pred.Kind == PredicateKind.Within && pred.WithinValues != null)
            {
                var values = pred.WithinValues;
                builder = new FilterOp(builder, _ => new PropertyWithinStringPredicate(capturedCol, capturedKey, values));
            }
            else
            {
                builder = new FilterOp(builder, _ => new PropertyInt64Predicate(capturedCol, capturedKey, capturedPred));
            }
        }

        return (PhysicalPlanner.Plan(builder, schema), varToColumn);
    }
}

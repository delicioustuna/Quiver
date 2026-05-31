using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api.Match;

internal static class MatchCompiler
{
    // Returns (plan, variable→column map).
    // For 2-node-1-edge: Full expand → [source(0), rel(1), neighbor(2)]
    internal static (IPhysicalOperator plan, Dictionary<string, int> varToColumn) Compile(
        IGraphTransaction tx,
        ISchemaApi schema,
        GraphPattern pattern,
        List<(string variable, string key, PropertyPredicate pred)> wherePredicates)
    {
        var varToColumn = new Dictionary<string, int>();

        IOperatorBuilder builder;
        var startNode = pattern.StartNode;

        builder = startNode.Label != null
            ? new ScanBuilder(startNode.Label)
            : new ScanBuilder();

        if (pattern.Edge == null || pattern.EndNode == null)
        {
            // Single-node pattern
            varToColumn[startNode.Variable] = 0;
        }
        else
        {
            // 2-node 1-edge pattern; use Full output to keep all three columns
            var edge = pattern.Edge;
            var endNode = pattern.EndNode;
            var direction = edge.Outgoing ? Direction.Outgoing : Direction.Incoming;

            // Full expand: col0=source, col1=rel, col2=neighbor
            builder = new ExpandBuilder(builder, direction, edge.Type, ExpandOutputMode.Full);
            varToColumn[startNode.Variable] = 0;
            varToColumn[endNode.Variable]   = 2;

            // Filter end node by label
            if (endNode.Label != null)
            {
                var labelId = schema.GetOrCreateLabel(endNode.Label);
                builder = new FilterBuilder(builder, _ => new LabelPredicate(labelId, column: 2));
            }
        }

        // Apply WHERE predicates
        foreach (var (variable, key, pred) in wherePredicates)
        {
            if (!varToColumn.TryGetValue(variable, out int entityCol))
                throw new InvalidOperationException($"Unknown variable '{variable}' in WHERE clause.");

            var keyId = schema.GetOrCreatePropertyKey(key);
            var capturedCol  = entityCol;
            var capturedKey  = keyId;
            var capturedPred = pred;

            IOperatorBuilder filter;
            if (pred.Kind == PredicateKind.Eq && pred.StringValue != null)
            {
                var strVal = pred.StringValue;
                filter = new FilterBuilder(builder, _ => new PropertyEqStringPredicate(capturedCol, capturedKey, strVal));
            }
            else if (pred.Kind == PredicateKind.Within && pred.WithinValues != null)
            {
                var values = pred.WithinValues;
                filter = new FilterBuilder(builder, _ => new PropertyWithinStringPredicate(capturedCol, capturedKey, values));
            }
            else
            {
                filter = new FilterBuilder(builder, _ => new PropertyInt64Predicate(capturedCol, capturedKey, capturedPred));
            }
            builder = filter;
        }

        return (builder.Build(schema), varToColumn);
    }
}

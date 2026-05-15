using Quiver.Client.Internal;
using Quiver.Core;

namespace Quiver.Client;

/// <summary>
/// GC-1: <c>.is(value)</c> step for property-value traversals. Rewrites
/// <c>t.Values(key).Is(v)</c> into <c>t.Has(key, v).Values(key)</c> so the
/// equality check happens at the property store (where the bytes live)
/// rather than as a post-projection filter on the tuple stream — the single-
/// pass Volcano model has no way to surface UTF-8 bytes back through a
/// generic predicate.
/// </summary>
public static class IsExtensions
{
    /// <summary>GC-1: string-value equality filter for <c>.Values(key).Is(value)</c>.</summary>
    public static GraphTraversal<string> Is(this GraphTraversal<string> traversal, string value)
    {
        ArgumentNullException.ThrowIfNull(traversal);
        ArgumentNullException.ThrowIfNull(value);
        if (traversal._builder is not PropertyLookupBuilder lookup)
            throw new InvalidOperationException(
                "GraphTraversal<string>.Is(value) is only valid immediately after .Values(key). " +
                "Other string sources (e.g. .Label()) do not preserve a property-key context to filter on.");

        var keyId = traversal._schema.GetOrCreatePropertyKey(lookup.Key);
        var sourceCol = lookup.Source.CurrentEntityColumn;
        var filtered = new FilterBuilder(
            lookup.Source,
            _ => new PropertyEqStringPredicate(sourceCol, keyId, value));
        var rebuiltLookup = new PropertyLookupBuilder(filtered, lookup.Key);
        int valueCol = rebuiltLookup.PredictedOutputColumnCount - 1;
        return new GraphTraversal<string>(
            traversal._tx, traversal._schema, rebuiltLookup,
            row => row.GetString(valueCol), traversal._entityColumn);
    }

    /// <summary>GC-1: int64-value equality filter for <c>.Values(key).Is(value)</c>.</summary>
    public static GraphTraversal<long> Is(this GraphTraversal<long> traversal, long value)
    {
        ArgumentNullException.ThrowIfNull(traversal);
        if (traversal._builder is not PropertyLookupBuilder lookup)
            throw new InvalidOperationException(
                "GraphTraversal<long>.Is(value) is only valid immediately after .Values(key) or .Id(). " +
                "Use .Has(key, P.Eq(value)) directly when filtering raw ids.");

        var keyId = traversal._schema.GetOrCreatePropertyKey(lookup.Key);
        var sourceCol = lookup.Source.CurrentEntityColumn;
        var pred = P.Eq(value);
        var filtered = new FilterBuilder(
            lookup.Source,
            _ => new PropertyInt64Predicate(sourceCol, keyId, pred));
        var rebuiltLookup = new PropertyLookupBuilder(filtered, lookup.Key);
        int valueCol = rebuiltLookup.PredictedOutputColumnCount - 1;
        return new GraphTraversal<long>(
            traversal._tx, traversal._schema, rebuiltLookup,
            row => row.GetInt64(valueCol), traversal._entityColumn);
    }
}

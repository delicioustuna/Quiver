using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// SID-style join index from
/// <see cref="RelationshipId"/> to a scalar property value. Built once and
/// kept alongside a snapshot so weighted traversals, edge filters, and
/// algorithm kernels can resolve <c>edge.weight</c> without walking the
/// per-relationship property chain on every lookup.
/// </summary>
/// <remarks>
/// Scope is intentionally narrow: this index targets hot paths that already
/// hold a <see cref="RelationshipId"/> and want a scalar property in O(1).
/// It is not a general replacement for
/// <see cref="IPropertyStore.Enumerate"/> — string / bytes / array values
/// are out of scope, and <see cref="PropertyLookupOperator"/>'s full
/// surface still goes through the chain.
///
/// The cursor-based read path (<see cref="AdjacencyCursor.WeightRaw"/> from
/// an <see cref="IAdjacencyPayloadView"/>) already serves the
/// "iterate-while-reading-weight" case. This index complements it for the
/// "I have a relationship id, give me the value" case which the payload
/// lane (keyed by node) cannot answer in O(1).
/// </remarks>
public interface IRelationshipPropertyJoinIndex
{
    /// <summary>
    /// Look up the scalar value for <paramref name="relationshipId"/> /
    /// <paramref name="keyId"/>. Returns false when the index has no entry
    /// (relationship out of range, property absent, or key not covered by
    /// this index instance). On true, <paramref name="type"/> is the
    /// observed scalar type and <paramref name="scalarBits"/> is the raw
    /// bit pattern — reinterpret via <see cref="BitConverter.Int64BitsToDouble"/>
    /// for <see cref="PropertyValueType.Double"/>.
    /// </summary>
    bool TryGetScalar(
        RelationshipId relationshipId,
        PropertyKeyId keyId,
        out PropertyValueType type,
        out long scalarBits);

    /// <summary>The property key this index was built for.</summary>
    PropertyKeyId KeyId { get; }

    /// <summary>The scalar type covered by this index.</summary>
    PropertyValueType ValueType { get; }

    /// <summary>Number of relationships with a recorded value.</summary>
    long EntryCount { get; }
}

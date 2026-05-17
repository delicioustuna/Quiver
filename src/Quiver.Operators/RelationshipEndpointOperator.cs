using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// GC-1: resolves a relationship-id column into a node-id column by looking
/// up the requested endpoint (source / target / "other" relative to the
/// inbound traversal direction). Implements Gremlin's <c>.outV()</c> /
/// <c>.inV()</c> / <c>.otherV()</c> steps when chained after
/// <c>.OutRelationships()</c> / <c>.InRelationships()</c> / <c>.BothRelationships()</c>.
/// </summary>
/// <remarks>
/// For <see cref="RelationshipEndpoint.Other"/> we don't know which side of
/// the edge the caller came from at runtime, so the operator returns
/// <c>Target</c> when the relationship originates from the rel's source
/// chain — matching the BothE walk pattern. Callers wanting a strict
/// "other than X" relative to a known node should use the typed
/// <see cref="ExpandOperator"/> output mode instead.
/// </remarks>
public sealed class RelationshipEndpointOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _relColumn;
    private readonly RelationshipEndpoint _endpoint;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public RelationshipEndpointOperator(IPhysicalOperator source, int relColumn, RelationshipEndpoint endpoint)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _relColumn = relColumn;
        _endpoint = endpoint;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
    }

    public bool MoveNext()
    {
        if (!_source.MoveNext()) return false;
        var relId = new RelationshipId(_source.Current[_relColumn].LongValue);
        var rel = _tx!.Relationships.Read(relId);
        long endpointId = _endpoint switch
        {
            RelationshipEndpoint.Source => rel.Source.Value,
            RelationshipEndpoint.Target => rel.Target.Value,
            // "Other" without a context node is ambiguous; default to target
            // since OutE/BothE chains usually want "the far side from source".
            _ => rel.Target.Value,
        };
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = endpointId };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() => _source.Dispose();
}

public enum RelationshipEndpoint : byte
{
    Source = 1,
    Target = 2,
    Other = 3,
}

using Quiver;
using Quiver.Core;
using Quiver.Operators;

namespace Quiver.Client;

/// <summary>
/// GC-6: typed accessor handed to <c>.Select&lt;TResult&gt;(Func&lt;MatchTuple, TResult&gt;)</c>.
/// Resolves alias names (bound earlier with <c>.As(name)</c>) to the carried
/// tuple slot for that name. Unknown aliases throw
/// <see cref="InvalidOperationException"/>; type-mismatched accessors throw
/// <see cref="InvalidCastException"/> via the slot's declared type.
/// </summary>
public readonly struct MatchTuple
{
    private readonly QueryRow _row;
    private readonly Dictionary<string, int> _aliases;

    internal MatchTuple(QueryRow row, Dictionary<string, int> aliases)
    {
        _row = row;
        _aliases = aliases;
    }

    private int Column(string alias)
    {
        if (!_aliases.TryGetValue(alias, out var col))
            throw new InvalidOperationException($"Alias '{alias}' is not defined.");
        return col;
    }

    public NodeId Node(string alias) => _row.GetNodeId(Column(alias));

    public RelationshipId Relationship(string alias) => _row.GetRelationshipId(Column(alias));

    public long Int64(string alias) => _row.GetInt64(Column(alias));

    public string String(string alias) => _row.GetString(Column(alias));

    public bool Boolean(string alias) => _row.GetBoolean(Column(alias));

    public double Double(string alias) => _row.GetDouble(Column(alias));

    public TupleSlotType TypeOf(string alias) => _row.GetSlotType(Column(alias));
}

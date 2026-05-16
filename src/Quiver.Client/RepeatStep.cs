using Quiver.Stores;

namespace Quiver.Client;

/// <summary>
/// GC-4: fluent recorder for <c>.Repeat(s => s.Out("KNOWS")).Times(n)</c>.
/// Only single-step expansions (one of <see cref="Out"/> / <see cref="In"/> /
/// <see cref="Both"/>) are recorded; the last call wins. Filters or chained
/// expansions inside the closure are not supported in Phase 1 because the
/// underlying <c>VariableLengthExpandOperator</c> takes a single direction +
/// type.
/// </summary>
public sealed class RepeatStep
{
    internal Direction Direction { get; private set; } = Direction.Outgoing;
    internal string? TypeFilter { get; private set; }

    /// <summary>Outgoing single-hop step (Gremlin <c>out()</c>).</summary>
    public RepeatStep Out(string? type = null)
    {
        Direction = Direction.Outgoing;
        TypeFilter = type;
        return this;
    }

    /// <summary>Incoming single-hop step (Gremlin <c>in()</c>).</summary>
    public RepeatStep In(string? type = null)
    {
        Direction = Direction.Incoming;
        TypeFilter = type;
        return this;
    }

    /// <summary>Undirected single-hop step (Gremlin <c>both()</c>).</summary>
    public RepeatStep Both(string? type = null)
    {
        Direction = Direction.Both;
        TypeFilter = type;
        return this;
    }
}

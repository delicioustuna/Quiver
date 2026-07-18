using Quiver.Api;
using Quiver.Transactions;

namespace Quiver.Studio.Models;

public sealed class ScriptGlobals
{
    public required QuiverDatabase db { get; init; }
    public required IReadTransaction tx { get; init; }
    public required GraphTraversalSource g { get; init; }
    public required ISchemaCatalog schema { get; init; }
}

namespace Quiver.Core;

/// <summary>
/// Lightweight (kind, id) handle used by VEC-4 helpers so node and
/// relationship paths share a single enumeration / lookup shape without
/// pulling in <c>NodeId</c> / <c>RelationshipId</c> from upper assemblies.
/// </summary>
public readonly record struct EntityRef(EntityKind Kind, long Id);

/// <summary>
/// Thin abstraction the <c>Quiver.Embedding</c> helper depends on instead of
/// reaching into <c>Quiver.GraphDatabase</c> directly. The adapter lets the
/// helper stay decoupled from engine internals — see codex_advice_3.md §6.6
/// and §6.8 (VEC-4).
/// </summary>
/// <remarks>
/// The engine owns the vector store, vector catalog, and the ability to open
/// short-lived read sessions for the scan / backfill path (Z'). Post-commit
/// hooks (Y') are wired by the caller through <c>ICommitHookRegistrar</c>
/// directly on the transaction — the engine does not have to mediate that.
/// </remarks>
public interface IGraphEngine
{
    IVectorStore Vectors { get; }

    IVectorCatalog Catalog { get; }

    /// <summary>
    /// Open a read-only session over the graph for enumerating entities and
    /// reading their source-text property. Session must be disposed to
    /// release the underlying transaction.
    /// </summary>
    IGraphEngineReadSession BeginRead();
}

/// <summary>
/// One-shot read session used by <c>ScanAndEnqueueAsync</c>. Exposes only the
/// operations the embedding helper needs — full enumeration and string
/// property lookup — so future engine-internal changes do not ripple into
/// the helper assembly.
/// </summary>
public interface IGraphEngineReadSession : IDisposable
{
    /// <summary>
    /// Enumerate every live entity of the requested kind. Order is
    /// implementation-defined; the helper does not rely on it.
    /// </summary>
    IEnumerable<EntityRef> EnumerateEntities(EntityKind kind);

    /// <summary>
    /// Read a UTF-8 string property by name. Returns false when the entity
    /// has no such property or when the property is not a string — non-string
    /// values are treated as "no source text" rather than coerced.
    /// </summary>
    bool TryReadStringProperty(EntityRef entity, string propertyKey, out string text);
}

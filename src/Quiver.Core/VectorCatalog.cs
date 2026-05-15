namespace Quiver.Core;

/// <summary>
/// Lifecycle state of an embedding job for one (entity, index, provider).
/// Mirrors the helper-side enum in <c>Quiver.Embedding</c> (VEC-4) so the
/// catalog can be queried by the helper without a back-reference. VEC-2.
/// </summary>
public enum EmbeddingTaskState : byte
{
    NotStarted      = 0,
    InProgress      = 1,
    Completed       = 2,
    FailedRetryable = 3,
    FailedPermanent = 4,
    Stale           = 5,
}

/// <summary>
/// Identity of an embedding job. Same shape as the SQLite primary key in
/// <c>embedding_tasks</c>; using a record struct lets it act as a dictionary key
/// and an upsert filter without allocating.
/// </summary>
public readonly record struct EmbeddingTaskKey(
    EntityKind EntityKind,
    long EntityId,
    string IndexName,
    string ProviderId);

/// <summary>
/// Persisted state of an embedding job. <see cref="ContentHash"/> tracks the
/// last text successfully embedded so re-runs can detect staleness without
/// re-invoking the provider.
/// </summary>
public sealed record EmbeddingTaskRecord(
    EntityKind EntityKind,
    long EntityId,
    string IndexName,
    string ProviderId,
    EmbeddingTaskState State,
    string? ContentHash,
    string? LastError,
    DateTimeOffset LastUpdatedAtUtc)
{
    public EmbeddingTaskKey Key => new(EntityKind, EntityId, IndexName, ProviderId);
}

/// <summary>
/// Durable metadata for vector indexes and embedding-task lifecycle. The
/// catalog stores <see cref="VectorIndexSpec"/> definitions plus per-entity
/// task records; the actual vector payload and ANN index live elsewhere
/// (binary sidecar / future ANN backend) — see codex_advice_3.md §6.5.
/// VEC-2.
/// </summary>
public interface IVectorCatalog
{
    /// <summary>
    /// Register a new vector index. Throws <see cref="VectorException"/> when
    /// another index of the same name already exists, so duplicate-create is
    /// caught at the catalog rather than corrupting downstream state.
    /// </summary>
    void CreateIndex(VectorIndexSpec spec);

    /// <summary>
    /// Remove an index. Returns true when an entry was removed, false when no
    /// index of that name existed — callers decide whether the latter is an
    /// error in their context.
    /// </summary>
    bool DropIndex(string name);

    bool TryGetIndex(string name, out VectorIndexSpec spec);

    IReadOnlyList<VectorIndexSpec> ListIndexes();

    EmbeddingTaskRecord? GetTask(EmbeddingTaskKey key);

    /// <summary>
    /// Insert or replace a task record. Identity is the
    /// <c>(EntityKind, EntityId, IndexName, ProviderId)</c> tuple — same as
    /// the SQLite primary key.
    /// </summary>
    void UpsertTask(EmbeddingTaskRecord record);

    bool DeleteTask(EmbeddingTaskKey key);

    /// <summary>
    /// Enumerate task records, optionally constrained to a single index.
    /// Order is implementation-defined; callers needing a stable order must
    /// sort the result themselves.
    /// </summary>
    IEnumerable<EmbeddingTaskRecord> ListTasks(string? indexName = null);
}

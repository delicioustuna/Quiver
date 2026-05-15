namespace Quiver.Core;

/// <summary>
/// Identifies whether a vector belongs to a Node or a Relationship.
/// VEC-1; codex_advice_3.md §6.2.
/// </summary>
public enum EntityKind : byte
{
    Node = 1,
    Relationship = 2,
}

/// <summary>
/// Distance metric used by a vector index. The metric is fixed at index creation
/// and cannot be changed thereafter — query vectors are scored under the same
/// metric the index was built with.
/// </summary>
public enum DistanceMetric : byte
{
    Cosine = 1,
    Dot = 2,
    Euclidean = 3,
}

/// <summary>
/// Declarative specification of a vector index. Captured at index creation and
/// persisted by the backend catalog (VEC-2). <see cref="SourcePropertyKeyId"/>
/// points at the property whose value is the embedding source — provider /
/// normalization handling lives in <c>Quiver.Embedding</c> (VEC-4).
/// </summary>
public sealed record VectorIndexSpec(
    string Name,
    EntityKind EntityKind,
    PropertyKeyId SourcePropertyKeyId,
    int Dimensions,
    DistanceMetric Metric,
    string ProviderId,
    string? NormalizationProfile = null);

/// <summary>One KNN result row: which entity matched and its similarity score.</summary>
public readonly record struct VectorSearchResult(
    EntityKind EntityKind,
    long EntityId,
    float Score);

/// <summary>
/// Lazy cursor over KNN results. Mirrors <c>ExpandCursor</c> — call
/// <see cref="MoveNext"/> until it returns false, reading <see cref="Current"/>
/// each time. <see cref="Current"/> is only valid between successive
/// <see cref="MoveNext"/> calls.
/// </summary>
public abstract class VectorSearchCursor : IDisposable
{
    public abstract bool MoveNext();
    public abstract VectorSearchResult Current { get; }
    public virtual void Dispose() { }
}

/// <summary>
/// Minimal core contract for storing float vectors and running KNN. Text
/// processing, provider invocation, retry, and task-log are intentionally
/// excluded — those live in <c>Quiver.Embedding</c> (VEC-4).
/// VEC-1; codex_advice_3.md §6.3.
/// </summary>
public interface IVectorStore
{
    void CreateVectorIndex(VectorIndexSpec spec);

    void DropVectorIndex(string name);

    void SetVector(
        EntityKind kind,
        long entityId,
        string indexName,
        ReadOnlySpan<float> vector);

    void RemoveVector(EntityKind kind, long entityId, string indexName);

    VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k);
}

/// <summary>
/// Vector-store contract violations: unknown index, duplicate index name,
/// dimension mismatch, entity-kind mismatch, or invalid <c>k</c>.
/// </summary>
public sealed class VectorException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

namespace Quiver.Core;

// EntityKind is defined in EntityId.cs (FT-11). Vector code (VEC-1; codex_advice_3.md §6.2)
// only uses Node / Relationship; Property is reserved for diagnostics / catalog and is
// rejected by IVectorStore implementations.

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

    /// <summary>
    /// VEC-12: 登録済み index の <see cref="VectorIndexSpec"/> を取得する。
    /// optimizer / push-down rewrite が dim 等のメタ情報を必要とするために用いる。
    /// 既定実装は <c>false</c> を返す — メタを取得できない backend は dim awareness 無しの
    /// 旧経路にフォールバックする。
    /// </summary>
    bool TryGetIndex(string name, out VectorIndexSpec spec)
    {
        spec = default!;
        return false;
    }

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

    /// <summary>
    /// VEC-8: 同一インデックスに対する複数クエリを 1 回の呼び出しで投げる。
    /// <see cref="ReadOnlySpan{T}"/> は <see cref="IReadOnlyList{T}"/> に格納できないため、
    /// 入力は <see cref="ReadOnlyMemory{T}"/> 配列で受ける。既定実装は個別 <see cref="KnnSearch"/>
    /// を Q 回呼ぶフォールバック。in-memory backend は単一 snapshot 上で
    /// 「Q 個のクエリ × N 件のコーパス」を gather-then-score でまとめて評価する。
    /// </summary>
    /// <remarks>codex_advice_3.md §6.4。返却順序は入力 <paramref name="queries"/> と一致する。</remarks>
    IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var arr = new VectorSearchCursor[queries.Count];
        for (int i = 0; i < queries.Count; i++)
            arr[i] = KnnSearch(indexName, queries[i].Span, k);
        return arr;
    }
}

/// <summary>
/// Vector-store contract violations: unknown index, duplicate index name,
/// dimension mismatch, entity-kind mismatch, or invalid <c>k</c>.
/// </summary>
public sealed class VectorException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

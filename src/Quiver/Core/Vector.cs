namespace Quiver.Core;

// EntityKind is defined in EntityId.cs. Vector code (spec: 06_vector.md#vector-index)
// only uses Node / Relationship; Property is reserved for diagnostics / catalog and is
// rejected by IVectorStore implementations.

/// <summary>
/// ベクトルインデックスが用いる距離尺度。インデックス作成時に固定され以後変更できない —
/// クエリベクトルはインデックス構築時と同じ尺度でスコアリングされる。
/// </summary>
public enum DistanceMetric : byte
{
    /// <summary>コサイン類似度。</summary>
    Cosine = 1,
    /// <summary>内積 (ドット積)。</summary>
    Dot = 2,
    /// <summary>ユークリッド距離。</summary>
    Euclidean = 3,
}

/// <summary>
/// ベクトルインデックスの宣言的仕様。インデックス作成時に確定し backend カタログに永続化される。
/// <see cref="SourcePropertyKeyId"/> は埋め込み元となる値を持つプロパティを指す — プロバイダ /
/// 正規化の扱いは <c>Quiver.Embedding</c> 側にある。
/// </summary>
/// <param name="Name">インデックス名 (一意)。</param>
/// <param name="EntityKind">対象エンティティ種別 (Node / Relationship)。</param>
/// <param name="SourcePropertyKeyId">埋め込み元の値を持つプロパティキー。</param>
/// <param name="Dimensions">ベクトルの次元数。</param>
/// <param name="Metric">スコアリングに使う距離尺度。</param>
/// <param name="ProviderId">埋め込みプロバイダ識別子。</param>
/// <param name="NormalizationProfile">正規化プロファイル名 (任意)。</param>
public sealed record VectorIndexSpec(
    string Name,
    EntityKind EntityKind,
    PropertyKeyId SourcePropertyKeyId,
    int Dimensions,
    DistanceMetric Metric,
    string ProviderId,
    string? NormalizationProfile = null);

/// <summary>KNN 検索の 1 行: どのエンティティがマッチしたかと、その類似度スコア。</summary>
/// <param name="EntityKind">マッチしたエンティティの種別。</param>
/// <param name="EntityId">マッチしたエンティティの ID。</param>
/// <param name="Score">類似度スコア。</param>
public readonly record struct VectorSearchResult(
    EntityKind EntityKind,
    long EntityId,
    float Score);

/// <summary>
/// KNN 結果を遅延列挙するカーソル。<see cref="MoveNext"/> が <c>false</c> を返すまで呼び続け、
/// 各回 <see cref="Current"/> を読む。<see cref="Current"/> は連続する <see cref="MoveNext"/>
/// 呼び出しの間のみ有効。
/// </summary>
public abstract class VectorSearchCursor : IDisposable
{
    /// <summary>次の結果へ進む。結果があれば <c>true</c>、列挙完了で <c>false</c>。</summary>
    public abstract bool MoveNext();
    /// <summary>現在指している結果行。</summary>
    public abstract VectorSearchResult Current { get; }
    /// <summary>カーソルを破棄する (既定は no-op)。</summary>
    public virtual void Dispose() { }
}

/// <summary>
/// float ベクトルの格納と KNN 実行を担う最小のコア契約。テキスト処理 / プロバイダ呼び出し /
/// リトライ / タスクログは意図的に除外され、それらは <c>Quiver.Embedding</c> にある
/// 。
/// </summary>
public interface IVectorStore
{
    /// <summary>新しいベクトルインデックスを作成する。</summary>
    void CreateVectorIndex(VectorIndexSpec spec);

    /// <summary>指定名のベクトルインデックスを削除する。</summary>
    void DropVectorIndex(string name);

    /// <summary>
    /// 登録済み index の <see cref="VectorIndexSpec"/> を取得する。
    /// optimizer / push-down rewrite が dim 等のメタ情報を必要とするために用いる。
    /// 既定実装は <c>false</c> を返す — メタを取得できない backend は dim awareness 無しの
    /// 旧経路にフォールバックする。
    /// </summary>
    bool TryGetIndex(string name, out VectorIndexSpec spec)
    {
        spec = default!;
        return false;
    }

    /// <summary>指定エンティティのベクトルを設定 (上書き) する。</summary>
    void SetVector(
        EntityKind kind,
        long entityId,
        string indexName,
        ReadOnlySpan<float> vector);

    /// <summary>指定エンティティのベクトルをインデックスから除去する。</summary>
    void RemoveVector(EntityKind kind, long entityId, string indexName);

    /// <summary>クエリベクトルに対する上位 <paramref name="k"/> 件の近傍を検索する。</summary>
    VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k);

    /// <summary>
    /// 同一インデックスに対する複数クエリを 1 回の呼び出しで投げる。
    /// <see cref="ReadOnlySpan{T}"/> は <see cref="IReadOnlyList{T}"/> に格納できないため、
    /// 入力は <see cref="ReadOnlyMemory{T}"/> 配列で受ける。既定実装は個別 <see cref="KnnSearch"/>
    /// を Q 回呼ぶフォールバック。in-memory backend は単一 snapshot 上で
    /// 「Q 個のクエリ × N 件のコーパス」を gather-then-score でまとめて評価する。
    /// </summary>
    /// <remarks>返却順序は入力 <paramref name="queries"/> と一致する。</remarks>
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
/// ベクトルストア契約違反 (未知のインデックス、重複名、次元不一致、エンティティ種別不一致、
/// 不正な <c>k</c> 等) を表す例外。
/// </summary>
public sealed class VectorException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

namespace Quiver.Core;

// EntityKind は EntityId.cs で定義。ベクトルコードは Node / Relationship のみを使用し、
// Property は診断 / カタログ用に予約されている (IVectorStore 実装は拒否する)。

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
/// ベクトルインデックスの構造種別。<see cref="HnswFlat"/> は HNSW ANN グラフ + payload を保持し
/// KNN 検索 (vector-first / graph-first) の両方に使える。<see cref="FlatOnly"/> は payload のみ
/// 保持し HNSW を構築しない — <c>ApplyDyadic</c> (brute-force graph-first) 専用。
/// </summary>
public enum VectorIndexKind : byte
{
    /// <summary>HNSW ANN グラフ + payload (既定)。KNN / ApplyDyadic 両対応。</summary>
    HnswFlat = 0,
    /// <summary>payload のみ。HNSW を構築せず <c>SetVector</c> の upsert コストを削減する。
    /// vector-first <c>KnnSearch</c> は <see cref="VectorException"/> を投げる。</summary>
    FlatOnly = 1,
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
/// <param name="IndexKind">ベクトルインデックスの構造種別。</param>
/// <param name="HnswM">HNSW のレイヤ 1 以上で保持する最大近傍数。</param>
/// <param name="HnswMMax0">HNSW のレイヤ 0 で保持する最大近傍数。</param>
/// <param name="HnswMaxLayers">HNSW が保持できる最大レイヤ数。</param>
/// <param name="HnswEfConstruction">HNSW 構築時のビーム幅。</param>
public sealed record VectorIndexSpec(
    string Name,
    EntityKind EntityKind,
    PropertyKeyId SourcePropertyKeyId,
    int Dimensions,
    DistanceMetric Metric,
    string ProviderId,
    string? NormalizationProfile = null,
    VectorIndexKind IndexKind = VectorIndexKind.HnswFlat,
    int HnswM = 16,
    int HnswMMax0 = 32,
    int HnswMaxLayers = 8,
    int HnswEfConstruction = 200);

internal static class VectorIndexSpecValidator
{
    public static void Validate(VectorIndexSpec spec)
    {
        if (string.IsNullOrEmpty(spec.Name))
            throw new VectorException("Vector index name must not be empty.");
        if (spec.Dimensions <= 0)
            throw new VectorException(
                $"Vector index '{spec.Name}' must have positive dimensions (was {spec.Dimensions}).");
        if (spec.HnswM is < 2 or > byte.MaxValue)
            throw Invalid(spec, nameof(spec.HnswM), spec.HnswM, "2..255");
        if (spec.HnswMMax0 < spec.HnswM || spec.HnswMMax0 > byte.MaxValue)
            throw Invalid(spec, nameof(spec.HnswMMax0), spec.HnswMMax0, $"{spec.HnswM}..255");
        if (spec.HnswMaxLayers is < 1 or > byte.MaxValue)
            throw Invalid(spec, nameof(spec.HnswMaxLayers), spec.HnswMaxLayers, "1..255");
        if (spec.HnswEfConstruction < spec.HnswM || spec.HnswEfConstruction > 1_000_000)
            throw Invalid(
                spec,
                nameof(spec.HnswEfConstruction),
                spec.HnswEfConstruction,
                $"{spec.HnswM}..1000000");
    }

    private static VectorException Invalid(
        VectorIndexSpec spec,
        string parameter,
        int value,
        string expected) =>
        new(
            $"Vector index '{spec.Name}' has invalid {parameter}={value}; expected {expected}.");
}

/// <summary>KNN 検索の 1 行: どのエンティティがマッチしたかと、その類似度スコア。</summary>
/// <param name="EntityKind">マッチしたエンティティの種別。</param>
/// <param name="EntityId">マッチしたエンティティの ID。</param>
/// <param name="Score">類似度スコア。</param>
public readonly record struct VectorSearchResult(
    EntityKind EntityKind,
    long EntityId,
    float Score);

/// <summary>
/// KNN 検索の精度と探索量を制御する実行時オプション。
/// 永続フォーマットやインデックス構築品質には影響しない。
/// </summary>
public sealed class VectorSearchOptions
{
    /// <summary>
    /// HNSW の探索ビーム幅。大きいほど再現率が上がりやすい一方、検索時間と一時メモリが増える。
    /// 既定値 200 は従来の固定値と同一。
    /// </summary>
    public int EfSearch { get; init; } = 200;

    /// <summary>
    /// フィルタ付き HNSW 検索で <c>k</c> に掛ける候補のオーバーサンプル係数。
    /// 既定値 8 は従来の固定値と同一。
    /// </summary>
    public int FilteredOversampleFactor { get; init; } = 8;
}

internal static class VectorSearchOptionsValidator
{
    private static readonly VectorSearchOptions DefaultOptions = new();

    public static VectorSearchOptions Normalize(VectorSearchOptions? options)
    {
        options ??= DefaultOptions;
        if (options.EfSearch is < 1 or > 1_000_000)
            throw new VectorException(
                $"{nameof(VectorSearchOptions.EfSearch)} must be in 1..1000000 " +
                $"(was {options.EfSearch}).");
        if (options.FilteredOversampleFactor is < 1 or > 1_024)
            throw new VectorException(
                $"{nameof(VectorSearchOptions.FilteredOversampleFactor)} must be in 1..1024 " +
                $"(was {options.FilteredOversampleFactor}).");
        return options;
    }
}

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

    /// <summary>登録済みベクトルインデックスの一覧を返す。</summary>
    IReadOnlyList<VectorIndexSpec> ListVectorIndexes() => [];

    /// <summary>指定エンティティのベクトルを設定 (上書き) する。</summary>
    void SetVector(
        EntityKind kind,
        long entityId,
        string indexName,
        ReadOnlySpan<float> vector);

    /// <summary>指定エンティティのベクトルをインデックスから除去する。</summary>
    void RemoveVector(EntityKind kind, long entityId, string indexName);

    /// <summary>
    /// 指定エンティティの格納ベクトルを <paramref name="destination"/> へ読み出す。
    /// alloc-free — 呼び出し側がインデックスの次元数以上のバッファを用意する。
    /// 未設定 / 削除済み / 世代不一致 (slot 再利用による stale binding) は <c>false</c>。
    /// </summary>
    bool TryGetVector(EntityKind kind, long entityId, string indexName, Span<float> destination)
        => false;

    /// <summary>クエリベクトルに対する上位 <paramref name="k"/> 件の近傍を検索する。</summary>
    VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null);

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
        int k,
        VectorSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var arr = new VectorSearchCursor[queries.Count];
        for (int i = 0; i < queries.Count; i++)
            arr[i] = KnnSearch(indexName, queries[i].Span, k, options);
        return arr;
    }
}

/// <summary>
/// ベクトルストア契約違反 (未知のインデックス、重複名、次元不一致、エンティティ種別不一致、
/// 不正な <c>k</c> 等) を表す例外。
/// </summary>
public sealed class VectorException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

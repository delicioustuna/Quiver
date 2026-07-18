namespace Quiver.Core;

// EntityKind は EntityId.cs で定義。ベクトルコードは Vertex / Edge / Nexus を使用する。

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

// ElementType は現時点で Float32 のみだが、量子化埋め込み (int8 / binary 等) をモデル側が
// 直接出力する将来に備え、格納表現をインデックス作成時の契約として今のうちに固定しておく。
// これにより新しい表現の追加が「既存 DB の再解釈」ではなく「新フィールド値の追加」になり、
// 旧バージョンのリーダーも未知の表現を明確なエラーで拒否できる。演算経路の抽象化
// (スコアリングカーネルの切替) は 2 つ目の表現を実装するときに内部リファクタとして導入する。

/// <summary>
/// ベクトルの要素がインデックス内でどう表現されるか (格納と距離計算の数値型)。
/// インデックス作成時に固定され、以後変更できない。
/// </summary>
/// <remarks>
/// 現在サポートされるのは <see cref="Float32"/> のみ。このプロパティは、埋め込みモデルが
/// 量子化ベクトル (8 ビット整数など) を直接出力する場合に将来対応できるよう、
/// フォーマット契約として予約されている。未対応の値を指定すると
/// <see cref="VectorException"/> が発生する。
/// </remarks>
public enum VectorElementType : byte
{
    /// <summary>32 ビット浮動小数点 (IEEE 754 single)。既定。</summary>
    Float32 = 0,
}

internal sealed record VectorIndexDescriptor(
    string Name,
    EntityKind OwnerKind,
    PropertyKeyId TargetPropertyKeyId,
    string? TargetScope,
    int Dimensions,
    DistanceMetric Metric,
    VectorElementType ElementType = VectorElementType.Float32,
    int HnswM = 32,
    int HnswMMax0 = 64,
    int HnswMaxLayers = 8,
    int HnswEfConstruction = 400,
    VectorSegmentPolicy? SegmentPolicy = null);

internal static class VectorIndexDescriptorValidator
{
    public static void Validate(VectorIndexDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(descriptor.Name))
            throw new VectorException("Vector index name must not be empty.");
        // 新しい要素表現の追加時はここの許可リストを広げ、スコアリングカーネル /
        // payload レコード長 / cache slab の型をあわせて分岐させること。
        if (descriptor.ElementType != VectorElementType.Float32)
            throw new VectorException(
                $"Vector index '{descriptor.Name}' has unsupported element type " +
                $"{descriptor.ElementType}; this version supports only {VectorElementType.Float32}.");
        if (descriptor.Dimensions <= 0)
            throw new VectorException(
                $"Vector index '{descriptor.Name}' must have positive dimensions (was {descriptor.Dimensions}).");
        if (descriptor.HnswM is < 2 or > byte.MaxValue)
            throw Invalid(descriptor, nameof(descriptor.HnswM), descriptor.HnswM, "2..255");
        if (descriptor.HnswMMax0 < descriptor.HnswM || descriptor.HnswMMax0 > byte.MaxValue)
            throw Invalid(
                descriptor,
                nameof(descriptor.HnswMMax0),
                descriptor.HnswMMax0,
                $"{descriptor.HnswM}..255");
        if (descriptor.HnswMaxLayers is < 1 or > byte.MaxValue)
            throw Invalid(
                descriptor,
                nameof(descriptor.HnswMaxLayers),
                descriptor.HnswMaxLayers,
                "1..255");
        if (descriptor.HnswEfConstruction < descriptor.HnswM
            || descriptor.HnswEfConstruction > 1_000_000)
            throw Invalid(
                descriptor,
                nameof(descriptor.HnswEfConstruction),
                descriptor.HnswEfConstruction,
                $"{descriptor.HnswM}..1000000");
        VectorSegmentPolicy policy = descriptor.SegmentPolicy ?? new();
        if (policy.MaximumDeltaEntries <= 0)
            throw Invalid(
                descriptor,
                nameof(policy.MaximumDeltaEntries),
                policy.MaximumDeltaEntries,
                "1..2147483647");
        if (policy.MaximumSegments <= 0)
            throw Invalid(
                descriptor,
                nameof(policy.MaximumSegments),
                policy.MaximumSegments,
                "1..2147483647");
    }

    private static VectorException Invalid(
        VectorIndexDescriptor descriptor,
        string parameter,
        int value,
        string expected) =>
        new(
            $"Vector index '{descriptor.Name}' has invalid {parameter}={value}; expected {expected}.");
}

/// <summary>KNN 検索の 1 行: どのエンティティがマッチしたかと、その類似度スコア。</summary>
/// <param name="Owner">マッチしたownerのfull typed identity。</param>
/// <param name="Score">類似度スコア。</param>
public readonly record struct VectorSearchResult(
    EntityRef Owner,
    float Score)
{
    internal VectorSearchResult(EntityKind kind, long localId, float score)
        : this(EntityRef.Create(
            kind,
            EntityRef.UnpackSequence(localId),
            EntityRef.UnpackGeneration(localId)),
            score)
    {
    }

    internal EntityKind EntityKind => Owner.Kind;
    internal long EntityId => Owner.Value;
}

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
internal interface IVectorDefinitionCatalog
{
    void Create(VectorIndexDescriptor descriptor);

    void Drop(string name);

    bool TryGet(string name, out VectorIndexDescriptor descriptor);

    IReadOnlyList<VectorIndexDescriptor> List();

    void Reload();
}

/// <summary>
/// ベクトルストア契約違反 (未知のインデックス、重複名、次元不一致、エンティティ種別不一致、
/// 不正な <c>k</c> 等) を表す例外。
/// </summary>
public sealed class VectorException(string message, Exception? inner = null)
    : QuiverException(message, inner!);

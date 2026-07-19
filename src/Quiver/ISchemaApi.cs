using Quiver.Core;
using Quiver.Text;

namespace Quiver;

/// <summary>
/// トランザクションのスナップショットから token と index definition を参照するカタログ。
/// 未知の名前を参照しても token を作成しない。
/// </summary>
public interface ISchemaCatalog
{
    /// <summary>指定キーの多重度を返す。未登録キーは <see cref="PropertyCardinality.Single"/>。</summary>
    PropertyCardinality GetPropertyKeyCardinality(PropertyKeyId id);

    /// <summary>ラベル ID から名前へ逆引きする。未登録 ID では <c>null</c>。</summary>
    string? GetLabelName(LabelId id);

    /// <summary>自動作成せず、ラベル名から ID を引く。未登録なら <c>false</c>。</summary>
    bool TryGetLabelId(string name, out LabelId id);

    /// <summary>自動作成せず、プロパティキー名から ID を引く。未登録なら <c>false</c>。</summary>
    bool TryGetPropertyKeyId(string name, out PropertyKeyId id);

    /// <summary>自動作成せず、Edge型名から ID を引く。未登録なら <c>false</c>。</summary>
    bool TryGetEdgeTypeId(string name, out EdgeTypeId id);

    /// <summary>指定名の index definition が存在するかを返す。</summary>
    bool IndexExists(string indexName);

    /// <summary>登録済み scalar index definition の一覧を返す。</summary>
    IReadOnlyList<IndexInfo> ListIndexes();

    /// <summary>指定名の index definition と現在の artifact 状態を返す。</summary>
    bool TryGetIndex(string indexName, out IndexInfo info);

    /// <summary>登録済みラベル名の一覧を返す。</summary>
    IReadOnlyList<string> ListLabels();

    /// <summary>登録済みEdge型名の一覧を返す。</summary>
    IReadOnlyList<string> ListEdgeTypes();

    /// <summary>登録済みプロパティキー名の一覧を返す。</summary>
    IReadOnlyList<string> ListPropertyKeys();

    /// <summary>Nexus型 ID から名前へ逆引きする。未登録 ID では <c>null</c>。</summary>
    string? GetNexusTypeName(NexusTypeId id);

    /// <summary>自動作成せず、Nexus型名から ID を引く。未登録なら <c>false</c>。</summary>
    bool TryGetNexusTypeId(string name, out NexusTypeId id);

    /// <summary>登録済みNexus型名の一覧を返す。</summary>
    IReadOnlyList<string> ListNexusTypes();

    /// <summary>登録済みロール名の一覧を返す。</summary>
    IReadOnlyList<string> ListRoles();
}

/// <summary>
/// 書き込みトランザクションに所有される schema editor。
/// token と index definition の変更は所有トランザクションの commit/abort に従う。
/// </summary>
public interface ISchemaEditor : ISchemaCatalog
{
    /// <summary>ラベル名を ID に解決し、未登録なら新規発行する。</summary>
    LabelId GetOrCreateLabel(string name);

    /// <summary>Edge型名を ID に解決し、未登録なら新規発行する。</summary>
    EdgeTypeId GetOrCreateEdgeType(string name);

    /// <summary>プロパティキー名を ID に解決し、未登録なら single cardinality で新規発行する。</summary>
    PropertyKeyId GetOrCreatePropertyKey(string name);

    /// <summary>プロパティキー名を ID に解決し、未登録なら指定 cardinality で新規発行する。</summary>
    PropertyKeyId GetOrCreatePropertyKey(string name, PropertyCardinality cardinality);

    /// <summary>永続 scalar index definition を作成する。</summary>
    void CreateIndex(IndexDefinition definition);

    /// <summary>既存 index definition を削除する。</summary>
    void DropIndex(string indexName);

    /// <summary>ラベル名を変更する。</summary>
    bool RenameLabel(string oldName, string newName);

    /// <summary>プロパティキー名を変更する。</summary>
    bool RenamePropertyKey(string oldName, string newName);

    /// <summary>Edge型名を変更する。</summary>
    bool RenameEdgeType(string oldName, string newName);

    /// <summary>index definition 名を変更する。</summary>
    bool RenameIndex(string oldName, string newName);

    /// <summary>Nexus型名を ID に解決し、未登録なら新規発行する。</summary>
    NexusTypeId GetOrCreateNexusType(string name);
}

/// <summary>scalar index が比較に使う値と順序の種類。</summary>
public enum IndexKind
{
    /// <summary><see cref="int"/> の等値比較。</summary>
    Int32Equality,
    /// <summary><see cref="long"/> の等値比較。</summary>
    Int64Equality,
    /// <summary><see cref="double"/> の等値比較。</summary>
    DoubleEquality,
    /// <summary>文字列の等値比較。</summary>
    StringEquality,
    /// <summary>文字列の範囲比較。</summary>
    StringRange,
}

/// <summary>scalar index artifact の利用可能状態。</summary>
public enum IndexLifecycleState
{
    /// <summary>現在の primary data に対応し、query で利用できる。</summary>
    Ready,
    /// <summary>artifact を利用せず primary scan へ fallback する必要がある。</summary>
    RebuildRequired,
    /// <summary>再構築中であり、publish までは primary scan を使う。</summary>
    Building,
}

/// <summary>プロパティを所有する entity の範囲。</summary>
public enum PropertyOwnerKind
{
    /// <summary>Vertexプロパティ。</summary>
    Vertex,
    /// <summary>Edgeプロパティ。</summary>
    Edge,
    /// <summary>Nexusプロパティ。</summary>
    Nexus,
}

/// <summary>
/// scalar index の対象プロパティ。<see cref="Scope"/> が <c>null</c> の場合は
/// owner kind 全体を対象にし、指定時は label、Edge型、またはNexus型で限定する。
/// </summary>
/// <param name="OwnerKind">プロパティを所有する entity kind。</param>
/// <param name="PropertyKey">対象のプロパティキー名。</param>
/// <param name="Scope">任意のラベル、Edge型、またはNexus型名。</param>
public sealed record PropertyTarget(
    PropertyOwnerKind OwnerKind,
    string PropertyKey,
    string? Scope = null);

/// <summary>永続 index definition の共通情報。</summary>
/// <param name="Name">一意な index 名。</param>
/// <param name="Target">対象プロパティ。</param>
public abstract record IndexDefinition(string Name, PropertyTarget Target);

/// <summary>単一 scalar 値を B+Tree で検索する index definition。</summary>
/// <param name="Name">一意な index 名。</param>
/// <param name="Target">対象プロパティ。</param>
/// <param name="Kind">比較に使う scalar 型と検索モード。</param>
public sealed record ScalarIndexDefinition(
    string Name,
    PropertyTarget Target,
    IndexKind Kind) : IndexDefinition(Name, Target);

/// <summary>
/// immutable HNSW segment の構築と flat delta の統合方針。
/// </summary>
/// <param name="MaximumDeltaEntries">merge を開始する flat delta entry 数。</param>
/// <param name="MaximumSegments">検索対象 segment 数の上限。</param>
public sealed record VectorSegmentPolicy(
    int MaximumDeltaEntries = 4096,
    int MaximumSegments = 16);

/// <summary>vector property を検索する index definition。</summary>
/// <param name="Name">一意な index 名。</param>
/// <param name="Target">対象の vector property。</param>
/// <param name="Dimensions">vector の次元数。</param>
/// <param name="Metric">類似度の計算方法。</param>
/// <param name="ElementType">vector element の格納形式。</param>
/// <param name="HnswM">上位 layer の最大近傍数。</param>
/// <param name="HnswMMax0">layer 0 の最大近傍数。</param>
/// <param name="HnswMaxLayers">最大 layer 数。</param>
/// <param name="HnswEfConstruction">構築時の探索幅。</param>
/// <param name="SegmentPolicy">delta と segment の統合方針。</param>
public sealed record VectorIndexDefinition(
    string Name,
    PropertyTarget Target,
    int Dimensions,
    DistanceMetric Metric = DistanceMetric.Cosine,
    VectorElementType ElementType = VectorElementType.Float32,
    int HnswM = 32,
    int HnswMMax0 = 64,
    int HnswMaxLayers = 8,
    int HnswEfConstruction = 400,
    VectorSegmentPolicy? SegmentPolicy = null) : IndexDefinition(Name, Target);

/// <summary>全文 delta segment と immutable merge segment の統合方針。</summary>
/// <param name="MaximumDeltaEntries">merge を開始する文書変更数。</param>
/// <param name="MaximumSegments">検索対象 segment 数の上限。</param>
/// <param name="MaximumTombstoneRatio">merge を開始する tombstone 比率。</param>
public sealed record FullTextSegmentPolicy(
    int MaximumDeltaEntries = 4096,
    int MaximumSegments = 4,
    double MaximumTombstoneRatio = 0.30);

/// <summary>text property を BM25 で検索する全文 index definition。</summary>
/// <param name="Name">一意な index 名。</param>
/// <param name="Target">対象のtext property。</param>
/// <param name="TokenizerId">書込みと検索で共通利用するtokenizer ID。</param>
/// <param name="Filters">tokenizerの後段へ順に適用するfilter。</param>
/// <param name="K1">BM25のterm frequency飽和パラメータ。</param>
/// <param name="B">BM25の文書長正規化パラメータ。</param>
/// <param name="SegmentPolicy">deltaとsegmentの統合方針。</param>
public sealed record FullTextIndexDefinition(
    string Name,
    PropertyTarget Target,
    string TokenizerId = MixedBigramTokenizer.UnigramTokenizerId,
    IReadOnlyList<ITokenFilter>? Filters = null,
    double K1 = 1.2,
    double B = 0.75,
    FullTextSegmentPolicy? SegmentPolicy = null) : IndexDefinition(Name, Target);

/// <summary>登録済み index definition と artifact 状態。</summary>
/// <param name="Definition">永続 definition。</param>
/// <param name="State">artifact の lifecycle state。</param>
/// <param name="EntryCount">診断用の entry 数。</param>
public sealed record IndexInfo(
    IndexDefinition Definition,
    IndexLifecycleState State,
    long EntryCount)
{
    /// <summary>index 名。</summary>
    public string Name => Definition.Name;

    /// <summary>対象プロパティ。</summary>
    public PropertyTarget Target => Definition.Target;

    /// <summary>scalar index の比較方法。全文とvector indexでは<c>null</c>。</summary>
    public IndexKind? Kind => (Definition as ScalarIndexDefinition)?.Kind;
}

using Quiver.Core;

namespace Quiver;

/// <summary>
/// ラベル / Edge型 / プロパティキー / インデックスを管理するスキーマ API。
/// <see cref="QuiverDatabase.Schema"/> から取得する。
/// </summary>
public interface ISchemaApi
{
    /// <summary>ラベル名を ID に解決する (未登録の場合は新規発行)。</summary>
    LabelId GetOrCreateLabel(string name);

    /// <summary>Edge型名を ID に解決する (未登録の場合は新規発行)。</summary>
    EdgeTypeId GetOrCreateEdgeType(string name);

    /// <summary>プロパティキー名を ID に解決する (未登録の場合は <see cref="PropertyCardinality.Single"/> で新規発行)。</summary>
    PropertyKeyId GetOrCreatePropertyKey(string name);

    /// <summary>
    /// プロパティキー名を ID に解決する (未登録の場合は指定 cardinality で新規発行)。
    /// 既存キーで cardinality が一致すればそのまま返す。不一致なら <see cref="InvalidOperationException"/>。
    /// </summary>
    PropertyKeyId GetOrCreatePropertyKey(string name, PropertyCardinality cardinality);

    /// <summary>指定キーの多重度を返す。未登録キーは <see cref="PropertyCardinality.Single"/>。</summary>
    PropertyCardinality GetPropertyKeyCardinality(PropertyKeyId id);

    /// <summary>
    /// ラベル ID から名前へ逆引きする。未登録 ID では <c>null</c> を返す。
    /// ラベル名の射影や、ID を元の名前で表示したい診断系で利用する。
    /// </summary>
    string? GetLabelName(LabelId id);

    /// <summary>
    /// 自動作成せず、ラベル名から ID を引く。未登録なら <c>false</c>。
    /// MigrationContext が「rename が実際に状態を変えるかどうか」を判定するために使う。
    /// </summary>
    bool TryGetLabelId(string name, out LabelId id);

    /// <summary>自動作成せず、プロパティキー名から ID を引く。</summary>
    bool TryGetPropertyKeyId(string name, out PropertyKeyId id);

    /// <summary>自動作成せず、Edge型名から ID を引く。</summary>
    bool TryGetEdgeTypeId(string name, out EdgeTypeId id);

    /// <summary>指定名の索引が存在するか。AddIndex が新規作成になるかの判定用。</summary>
    bool IndexExists(string indexName);

    /// <summary>新規インデックスを作成する。</summary>
    void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind);

    /// <summary>既存インデックスを削除する。</summary>
    void DropIndex(string indexName);

    /// <summary>登録済みインデックスの一覧を返す。</summary>
    IReadOnlyList<IndexInfo> ListIndexes();

    /// <summary>
    /// 全文検索索引を作成する。<paramref name="label"/> / <paramref name="propertyKey"/> に
    /// 一致する文字列プロパティ書き込みが同一 Tx 内で転置インデックス (postings/norms) に維持される。
    /// <paramref name="options"/> でトークナイザ ID 等を指定する (既定は <c>mixed-bigram-v1</c>)。
    /// binary backend のみ対応。
    /// </summary>
    void CreateFullTextIndex(string indexName, string label, string propertyKey, FullTextIndexOptions? options = null);

    /// <summary>登録済み全文索引の一覧を返す。</summary>
    IReadOnlyList<FullTextIndexInfo> ListFullTextIndexes();

    /// <summary>
    /// ラベル名を <paramref name="oldName"/> から <paramref name="newName"/> へ変更する。
    /// ラベル ID は維持されるため、既存Vertexのラベル所属関係は変更されない (テキスト表記のみ更新)。
    /// 旧名が無く新名が既にある場合は冪等な no-op として <c>true</c>。旧名も新名も無い場合は <c>false</c>。
    /// 新名が他のラベル ID に占有されているときは <see cref="InvalidOperationException"/>。
    /// </summary>
    bool RenameLabel(string oldName, string newName);

    /// <summary>プロパティキー名を rename する。意味論は <see cref="RenameLabel"/> と同じ。</summary>
    bool RenamePropertyKey(string oldName, string newName);

    /// <summary>Edge型名を rename する。意味論は <see cref="RenameLabel"/> と同じ。</summary>
    bool RenameEdgeType(string oldName, string newName);

    /// <summary>
    /// インデックス名を rename する。索引は <c>graph.quiver</c> 内テナントとして
    /// 同居するため、リネームはカタログ上の name 付け替えのみで完結し (テナント実体・ページ・WAL
    /// 整合性は不変)、物理ファイル rename は発生しない。
    /// </summary>
    bool RenameIndex(string oldName, string newName);

    /// <summary>登録済みラベル名の一覧を返す。</summary>
    IReadOnlyList<string> ListLabels();

    /// <summary>登録済みEdge型名の一覧を返す。</summary>
    IReadOnlyList<string> ListEdgeTypes();

    /// <summary>登録済みプロパティキー名の一覧を返す。</summary>
    IReadOnlyList<string> ListPropertyKeys();

    // ── Nexus型 / ロール ──────────────────────────────

    /// <summary>Nexus型名を ID に解決する (未登録の場合は新規発行)。</summary>
    NexusTypeId GetOrCreateNexusType(string name);

    /// <summary>Nexus型 ID から名前へ逆引きする。未登録 ID では <c>null</c>。</summary>
    string? GetNexusTypeName(NexusTypeId id);

    /// <summary>自動作成せず、Nexus型名から ID を引く。未登録なら <c>false</c>。</summary>
    bool TryGetNexusTypeId(string name, out NexusTypeId id);

    /// <summary>登録済みNexus型名の一覧を返す。</summary>
    IReadOnlyList<string> ListNexusTypes();

    /// <summary>登録済みロール名の一覧を返す。</summary>
    IReadOnlyList<string> ListRoles();
}

/// <summary>インデックス種別。プロパティ型と検索モード (等値 / 範囲) で分かれる。</summary>
public enum IndexKind
{
    /// <summary><see cref="int"/> 等値インデックス。</summary>
    Int32Equality,
    /// <summary><see cref="long"/> 等値インデックス。</summary>
    Int64Equality,
    /// <summary><see cref="double"/> 等値インデックス。</summary>
    DoubleEquality,
    /// <summary>文字列等値インデックス。</summary>
    StringEquality,
    /// <summary>文字列範囲インデックス (順序比較対応)。</summary>
    StringRange,
}

/// <summary>
/// <see cref="ISchemaApi.ListIndexes"/> が返す登録済みインデックスのメタ情報。
/// </summary>
public sealed record IndexInfo(
    string Name,
    string Label,
    string PropertyKey,
    IndexKind Kind,
    long EntryCount);

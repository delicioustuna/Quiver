using Quiver.Core;

namespace Quiver;

/// <summary>
/// ラベル / リレーションシップ型 / プロパティキー / インデックスを管理するスキーマ API。
/// <see cref="GraphDatabase.Schema"/> から取得する。
/// </summary>
public interface ISchemaApi
{
    /// <summary>ラベル名を ID に解決する (未登録の場合は新規発行)。</summary>
    LabelId GetOrCreateLabel(string name);

    /// <summary>リレーションシップ型名を ID に解決する (未登録の場合は新規発行)。</summary>
    RelationshipTypeId GetOrCreateRelationshipType(string name);

    /// <summary>プロパティキー名を ID に解決する (未登録の場合は新規発行)。</summary>
    PropertyKeyId GetOrCreatePropertyKey(string name);

    /// <summary>
    /// GC-1: ラベル ID から名前へ逆引きする。未登録 ID では <c>null</c> を返す。
    /// Gremlin の <c>.label()</c> ステップや、ID を元の名前で表示したい診断系で利用する。
    /// </summary>
    string? GetLabelName(LabelId id);

    /// <summary>
    /// OP-4: 自動作成せず、ラベル名から ID を引く。未登録なら <c>false</c>。
    /// MigrationContext が「rename が実際に状態を変えるかどうか」を判定するために使う。
    /// </summary>
    bool TryGetLabelId(string name, out LabelId id);

    /// <summary>OP-4: 自動作成せず、プロパティキー名から ID を引く。</summary>
    bool TryGetPropertyKeyId(string name, out PropertyKeyId id);

    /// <summary>OP-4: 自動作成せず、リレーションシップ型名から ID を引く。</summary>
    bool TryGetRelationshipTypeId(string name, out RelationshipTypeId id);

    /// <summary>OP-4: 指定名の索引が存在するか。AddIndex が新規作成になるかの判定用。</summary>
    bool IndexExists(string indexName);

    /// <summary>新規インデックスを作成する。</summary>
    void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind);

    /// <summary>既存インデックスを削除する。</summary>
    void DropIndex(string indexName);

    /// <summary>登録済みインデックスの一覧を返す。</summary>
    IReadOnlyList<IndexInfo> ListIndexes();

    /// <summary>
    /// OP-4: ラベル名を <paramref name="oldName"/> から <paramref name="newName"/> へ変更する。
    /// ラベル ID は維持されるため、既存ノードのラベル所属関係は変更されない (テキスト表記のみ更新)。
    /// 旧名が無く新名が既にある場合は冪等な no-op として <c>true</c>。旧名も新名も無い場合は <c>false</c>。
    /// 新名が他のラベル ID に占有されているときは <see cref="InvalidOperationException"/>。
    /// </summary>
    bool RenameLabel(string oldName, string newName);

    /// <summary>OP-4: プロパティキー名を rename する。意味論は <see cref="RenameLabel"/> と同じ。</summary>
    bool RenamePropertyKey(string oldName, string newName);

    /// <summary>OP-4: リレーションシップ型名を rename する。意味論は <see cref="RenameLabel"/> と同じ。</summary>
    bool RenameRelationshipType(string oldName, string newName);

    /// <summary>
    /// OP-4: インデックス名を rename する。索引ファイル (.idx / .idxmeta) は物理 rename され、
    /// fileKind は維持されるため WAL 整合性に影響しない。
    /// </summary>
    bool RenameIndex(string oldName, string newName);
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

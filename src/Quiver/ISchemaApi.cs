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

    /// <summary>新規インデックスを作成する。</summary>
    void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind);

    /// <summary>既存インデックスを削除する。</summary>
    void DropIndex(string indexName);

    /// <summary>登録済みインデックスの一覧を返す。</summary>
    IReadOnlyList<IndexInfo> ListIndexes();
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

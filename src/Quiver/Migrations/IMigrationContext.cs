using Quiver.Core;

namespace Quiver.Migrations;

/// <summary>
/// OP-4: <see cref="IMigration.ApplyAsync"/> / <see cref="IMigration.RevertAsync"/> に渡される
/// 操作面。スキーマ rename / 索引 add/drop / ノード走査を declarative にラップする。
/// </summary>
/// <remarks>
/// データミューテーション (<c>SetProperty</c>, <c>CreateRelationship</c>) は
/// <see cref="Transaction"/> を直接使うことを推奨する — context のヘルパは典型操作の
/// shortcut にすぎず、未提供のオペレーションは tx 経由でフルアクセス可能。
/// </remarks>
public interface IMigrationContext
{
    /// <summary>このマイグレーションがバインドされた書き込みトランザクション。</summary>
    IGraphTransaction Transaction { get; }

    /// <summary>このデータベースのスキーマ API (ラベル / プロパティキー / 索引)。</summary>
    ISchemaApi Schema { get; }

    /// <summary>マイグレーション ID (診断 / 例外メッセージ用)。</summary>
    string MigrationId { get; }

    /// <summary>ラベルを rename する shortcut。詳細は <see cref="ISchemaApi.RenameLabel"/>。</summary>
    bool RenameLabel(string oldName, string newName);

    /// <summary>プロパティキーを rename する shortcut。</summary>
    bool RenamePropertyKey(string oldName, string newName);

    /// <summary>リレーションシップ型を rename する shortcut。</summary>
    bool RenameRelationshipType(string oldName, string newName);

    /// <summary>索引を rename する shortcut。</summary>
    bool RenameIndex(string oldName, string newName);

    /// <summary>索引を追加する shortcut。詳細は <see cref="ISchemaApi.CreateIndex"/>。</summary>
    void AddIndex(string indexName, string label, string propertyKey, IndexKind kind);

    /// <summary>索引を削除する shortcut。詳細は <see cref="ISchemaApi.DropIndex"/>。</summary>
    void DropIndex(string indexName);

    /// <summary>
    /// 指定ラベルを持つ全ノードに対し <paramref name="action"/> を呼ぶ。
    /// 走査中に <see cref="Transaction"/> に対するミューテーションを行うのは安全だが、
    /// 同じノードの label / property を変更すると走査結果が二重に観測される可能性がある。
    /// </summary>
    void ForEachNode(string label, Action<NodeId> action);
}

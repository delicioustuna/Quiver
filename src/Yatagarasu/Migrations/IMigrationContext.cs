using Yatagarasu.Core;

namespace Yatagarasu.Migrations;

/// <summary>
/// <see cref="IMigration.ApplyAsync"/> / <see cref="IMigration.RevertAsync"/> に渡される
/// 操作面。スキーマ rename / 索引 add/drop / Vertex走査を declarative にラップする。
/// </summary>
/// <remarks>
/// データミューテーション (<c>SetProperty</c>, <c>CreateEdge</c>) は
/// <see cref="Transaction"/> を直接使うことを推奨する — context のヘルパは典型操作の
/// shortcut にすぎず、未提供のオペレーションは tx 経由でフルアクセス可能。
/// </remarks>
internal interface IMigrationContext
{
    /// <summary>このマイグレーションがバインドされた書き込みトランザクション。</summary>
    IWriteTransaction Transaction { get; }

    /// <summary>このデータベースのスキーマ API (ラベル / プロパティキー / 索引)。</summary>
    ISchemaEditor Schema { get; }

    /// <summary>マイグレーション ID (診断 / 例外メッセージ用)。</summary>
    string MigrationId { get; }

    /// <summary>ラベルを rename する shortcut。詳細は <see cref="ISchemaEditor.RenameLabel"/>。</summary>
    bool RenameLabel(string oldName, string newName);

    /// <summary>プロパティキーを rename する shortcut。</summary>
    bool RenamePropertyKey(string oldName, string newName);

    /// <summary>Edge型を rename する shortcut。</summary>
    bool RenameEdgeType(string oldName, string newName);

    /// <summary>索引を rename する shortcut。</summary>
    bool RenameIndex(string oldName, string newName);

    /// <summary>索引を追加する shortcut。詳細は <see cref="ISchemaEditor.CreateIndex"/>。</summary>
    void AddIndex(
        string indexName,
        string label,
        string propertyKey,
        IndexKind kind,
        bool unique = false);

    /// <summary>索引を削除する shortcut。詳細は <see cref="ISchemaEditor.DropIndex"/>。</summary>
    void DropIndex(string indexName);

    /// <summary>
    /// 指定ラベルを持つ全Vertexに対し <paramref name="action"/> を呼ぶ。
    /// 走査中に <see cref="Transaction"/> に対するミューテーションを行うのは安全だが、
    /// 同じVertexの label / property を変更すると走査結果が二重に観測される可能性がある。
    /// </summary>
    void ForEachVertex(string label, Action<VertexId> action);
}

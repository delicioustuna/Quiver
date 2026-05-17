namespace Quiver.Logical;

/// <summary>
/// BA-7 / codex_advice_3 8 節。<see cref="LogicalMutation"/> の判別子。
/// <see cref="IGraphTransaction"/> の公開ミューテーション API と対応する。
/// </summary>
public enum LogicalMutationKind : byte
{
    /// <summary>ノード作成。</summary>
    CreateNode = 1,
    /// <summary>ノード削除。</summary>
    DeleteNode = 2,
    /// <summary>リレーションシップ作成。</summary>
    CreateRelationship = 3,
    /// <summary>リレーションシップ削除。</summary>
    DeleteRelationship = 4,
    /// <summary>ノードプロパティの設定。</summary>
    SetNodeProperty = 5,
    /// <summary>リレーションシッププロパティの設定。</summary>
    SetRelationshipProperty = 6,
    /// <summary>ノードプロパティの削除。</summary>
    RemoveNodeProperty = 7,
}

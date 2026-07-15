namespace Quiver.Logical;

/// <summary>
/// <see cref="LogicalMutation"/> の判別子。
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
    /// <summary>ハイパーエッジ作成。</summary>
    CreateHyperedge = 8,
    /// <summary>ハイパーエッジ削除。</summary>
    DeleteHyperedge = 9,
    /// <summary>ハイパーエッジプロパティの設定 (Single cardinality)。</summary>
    SetHyperedgeProperty = 10,
    /// <summary>ハイパーエッジプロパティの削除 (Single cardinality)。</summary>
    RemoveHyperedgeProperty = 11,
    /// <summary>ハイパーエッジのマルチバリュープロパティへの値追加 (Set cardinality)。</summary>
    AddHyperedgePropertyValue = 12,
    /// <summary>ハイパーエッジのマルチバリュープロパティからの値除去 (Set cardinality)。</summary>
    RemoveHyperedgePropertyValue = 13,
}

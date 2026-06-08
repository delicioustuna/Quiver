using Quiver.Core;

namespace Quiver.Logical;

/// <summary>
/// 1 件のグラフミューテーションを表すセマンティックレコード。
///
/// 論理ミューテーションは書き込み中の <see cref="IGraphTransaction"/> によって、
/// 各公開ミューテーション呼び出しの後に生成され、コミットまでバッファされる。
/// 下層トランザクションが永続化コミットされる (バイナリバックエンドの場合は WAL フラッシュ、
/// SQLite バックエンドの場合は COMMIT) と、<see cref="ILogicalMutationSink"/> に渡される。
///
/// レコードは自己完結している — ラベル / リレーションシップ型 / プロパティキーの名称は
/// トークン ID ではなく文字列で保持するため、まだそれらトークンが未登録のグラフに対しても
/// ストリームを検査・送信・再生できる (トークン ID はソース DB とターゲット DB で異なる)。
/// </summary>
public readonly struct LogicalMutation
{
    /// <summary>ミューテーションの種別。</summary>
    public LogicalMutationKind Kind { get; }

    /// <summary>主要なノード ID (CreateNode / DeleteNode / *NodeProperty / CreateRelationship の source)。</summary>
    public NodeId NodeId { get; }

    /// <summary>CreateRelationship のターゲットノード。</summary>
    public NodeId TargetNodeId { get; }

    /// <summary>リレーションシップ ID (CreateRelationship の戻り値 / DeleteRelationship / SetRelationshipProperty)。</summary>
    public RelationshipId RelationshipId { get; }

    /// <summary><see cref="LogicalMutationKind.CreateNode"/> ではラベル名、<see cref="LogicalMutationKind.CreateRelationship"/> ではリレーションシップ型名。</summary>
    public string? TokenName { get; }

    /// <summary>*Property ミューテーションのプロパティキー名。</summary>
    public string? PropertyKey { get; }

    /// <summary><see cref="LogicalMutationKind.SetNodeProperty"/> / <see cref="LogicalMutationKind.SetRelationshipProperty"/> のプロパティ値。</summary>
    public LogicalPropertyValue PropertyValue { get; }

    private LogicalMutation(
        LogicalMutationKind kind,
        NodeId nodeId = default,
        NodeId targetNodeId = default,
        RelationshipId relationshipId = default,
        string? tokenName = null,
        string? propertyKey = null,
        LogicalPropertyValue propertyValue = default)
    {
        Kind = kind;
        NodeId = nodeId;
        TargetNodeId = targetNodeId;
        RelationshipId = relationshipId;
        TokenName = tokenName;
        PropertyKey = propertyKey;
        PropertyValue = propertyValue;
    }

    /// <summary>ノード作成のミューテーションレコードを生成する。</summary>
    public static LogicalMutation CreateNode(NodeId nodeId, string label)
        => new(LogicalMutationKind.CreateNode, nodeId: nodeId, tokenName: label);

    /// <summary>ノード削除のミューテーションレコードを生成する。</summary>
    public static LogicalMutation DeleteNode(NodeId nodeId)
        => new(LogicalMutationKind.DeleteNode, nodeId: nodeId);

    /// <summary>リレーションシップ作成のミューテーションレコードを生成する。</summary>
    public static LogicalMutation CreateRelationship(
        RelationshipId relId, NodeId source, NodeId target, string type)
        => new(LogicalMutationKind.CreateRelationship,
            nodeId: source, targetNodeId: target,
            relationshipId: relId, tokenName: type);

    /// <summary>リレーションシップ削除のミューテーションレコードを生成する。</summary>
    public static LogicalMutation DeleteRelationship(RelationshipId relId)
        => new(LogicalMutationKind.DeleteRelationship, relationshipId: relId);

    /// <summary>ノードプロパティ設定のミューテーションレコードを生成する。</summary>
    public static LogicalMutation SetNodeProperty(NodeId nodeId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.SetNodeProperty, nodeId: nodeId, propertyKey: key, propertyValue: value);

    /// <summary>リレーションシッププロパティ設定のミューテーションレコードを生成する。</summary>
    public static LogicalMutation SetRelationshipProperty(RelationshipId relId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.SetRelationshipProperty,
            relationshipId: relId, propertyKey: key, propertyValue: value);

    /// <summary>ノードプロパティ削除のミューテーションレコードを生成する。</summary>
    public static LogicalMutation RemoveNodeProperty(NodeId nodeId, string key)
        => new(LogicalMutationKind.RemoveNodeProperty, nodeId: nodeId, propertyKey: key);
}

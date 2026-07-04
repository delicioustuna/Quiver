using Quiver.Core;

namespace Quiver.Logical;

/// <summary>
/// キャプチャ済みの <see cref="LogicalMutation"/> ストリームを別の
/// <see cref="IGraphTransaction"/> に再適用するヘルパー。ターゲット DB が独自の ID を
/// 割り当てるため、ノード・リレーションシップ ID はオンザフライで再マッピングする。
/// 事前シード済みマップを渡すことで複数回の再生パスを連結できる。
/// </summary>
public static class LogicalMutationReplay
{
    /// <summary>
    /// <paramref name="mutations"/> を順に適用する。トランザクションの管理は呼び出し側の責務で、
    /// 書き込み可能な状態で渡し、再生完了後に commit (または rollback) する。
    /// これにより複数バッチを 1 つの外側トランザクションにまとめられる。
    /// </summary>
    /// <param name="tx">ターゲットトランザクション。</param>
    /// <param name="mutations">ミューテーションストリーム。通常は
    /// <see cref="ILogicalMutationSink"/> から取得する。</param>
    /// <param name="nodeMap">ソース ID → ターゲット ID のノードマップ (省略可)。呼び出し側で変更される。</param>
    /// <param name="relationshipMap">ソース ID → ターゲット ID のリレーションシップマップ (省略可)。呼び出し側で変更される。</param>
    /// <param name="hyperedgeMap">ソース ID → ターゲット ID のハイパーエッジマップ (省略可)。呼び出し側で変更される。</param>
    public static void Apply(
        IGraphTransaction tx,
        IEnumerable<LogicalMutation> mutations,
        IDictionary<long, NodeId>? nodeMap = null,
        IDictionary<long, RelationshipId>? relationshipMap = null,
        IDictionary<long, HyperedgeId>? hyperedgeMap = null)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(mutations);
        nodeMap ??= new Dictionary<long, NodeId>();
        relationshipMap ??= new Dictionary<long, RelationshipId>();
        hyperedgeMap ??= new Dictionary<long, HyperedgeId>();

        foreach (var m in mutations)
        {
            switch (m.Kind)
            {
                case LogicalMutationKind.CreateNode:
                {
                    var newId = tx.CreateNode(m.TokenName ?? string.Empty);
                    nodeMap[m.NodeId.Sequence] = newId;
                    break;
                }
                case LogicalMutationKind.DeleteNode:
                {
                    if (nodeMap.TryGetValue(m.NodeId.Sequence, out var nodeId))
                        tx.DeleteNode(nodeId);
                    break;
                }
                case LogicalMutationKind.CreateRelationship:
                {
                    if (!nodeMap.TryGetValue(m.NodeId.Sequence, out var src)) break;
                    if (!nodeMap.TryGetValue(m.TargetNodeId.Sequence, out var tgt)) break;
                    var newId = tx.CreateRelationship(src, tgt, m.TokenName ?? string.Empty);
                    relationshipMap[m.RelationshipId.Sequence] = newId;
                    break;
                }
                case LogicalMutationKind.DeleteRelationship:
                {
                    if (relationshipMap.TryGetValue(m.RelationshipId.Sequence, out var relId))
                        tx.DeleteRelationship(relId);
                    break;
                }
                case LogicalMutationKind.SetNodeProperty:
                {
                    if (!nodeMap.TryGetValue(m.NodeId.Sequence, out var nodeId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.SetProperty(nodeId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.SetRelationshipProperty:
                {
                    if (!relationshipMap.TryGetValue(m.RelationshipId.Sequence, out var relId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.SetProperty(relId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.RemoveNodeProperty:
                {
                    if (nodeMap.TryGetValue(m.NodeId.Sequence, out var nodeId))
                        tx.RemoveProperty(nodeId, m.PropertyKey ?? string.Empty);
                    break;
                }
                case LogicalMutationKind.CreateHyperedge:
                {
                    var source = m.Members;
                    if (source is null || source.Count == 0) break;
                    // メンバーの NodeId をターゲット DB の ID へ再マッピングする。
                    // 依存ノードが未再生 (マップ欠落) なら、このハイパーエッジは再生しない。
                    var remapped = new HyperedgeMember[source.Count];
                    bool complete = true;
                    for (int i = 0; i < source.Count; i++)
                    {
                        if (!nodeMap.TryGetValue(source[i].NodeId.Sequence, out var mapped))
                        {
                            complete = false;
                            break;
                        }
                        remapped[i] = new HyperedgeMember(source[i].Role, mapped);
                    }
                    if (!complete) break;
                    var newId = tx.CreateHyperedge(m.TokenName ?? string.Empty, remapped);
                    hyperedgeMap[m.HyperedgeId.Sequence] = newId;
                    break;
                }
                case LogicalMutationKind.DeleteHyperedge:
                {
                    if (hyperedgeMap.TryGetValue(m.HyperedgeId.Sequence, out var heId))
                        tx.DeleteHyperedge(heId);
                    break;
                }
                case LogicalMutationKind.SetHyperedgeProperty:
                {
                    if (!hyperedgeMap.TryGetValue(m.HyperedgeId.Sequence, out var heId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.SetProperty(heId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.RemoveHyperedgeProperty:
                {
                    if (hyperedgeMap.TryGetValue(m.HyperedgeId.Sequence, out var heId))
                        tx.RemoveProperty(heId, m.PropertyKey ?? string.Empty);
                    break;
                }
                case LogicalMutationKind.AddHyperedgePropertyValue:
                {
                    if (!hyperedgeMap.TryGetValue(m.HyperedgeId.Sequence, out var heId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.AddPropertyValue(heId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.RemoveHyperedgePropertyValue:
                {
                    if (!hyperedgeMap.TryGetValue(m.HyperedgeId.Sequence, out var heId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.RemovePropertyValue(heId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
            }
        }
    }
}

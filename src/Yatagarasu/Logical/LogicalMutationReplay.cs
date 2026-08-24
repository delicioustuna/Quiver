using Yatagarasu.Core;

namespace Yatagarasu.Logical;

/// <summary>
/// キャプチャ済みの <see cref="LogicalMutation"/> ストリームを別の
/// <see cref="IWriteTransaction"/> に再適用するヘルパー。ターゲット DB が独自の ID を
/// 割り当てるため、Vertex・Edge ID はオンザフライで再マッピングする。
/// 事前シード済みマップを渡すことで複数回の再生パスを連結できる。
/// </summary>
internal static class LogicalMutationReplay
{
    /// <summary>
    /// <paramref name="mutations"/> を順に適用する。トランザクションの管理は呼び出し側の責務で、
    /// 書き込み可能な状態で渡し、再生完了後に commit (または rollback) する。
    /// これにより複数バッチを 1 つの外側トランザクションにまとめられる。
    /// </summary>
    /// <param name="tx">ターゲットトランザクション。</param>
    /// <param name="mutations">ミューテーションストリーム。通常は
    /// <see cref="ILogicalMutationSink"/> から取得する。</param>
    /// <param name="vertexMap">ソースの世代込み packed ID → ターゲット ID のVertexマップ。</param>
    /// <param name="edgeMap">ソースの世代込み packed ID → ターゲット ID のEdgeマップ。</param>
    /// <param name="nexusMap">ソースの世代込み packed ID → ターゲット ID のNexusマップ。</param>
    public static void Apply(
        IWriteTransaction tx,
        IEnumerable<LogicalMutation> mutations,
        IDictionary<long, VertexId>? vertexMap = null,
        IDictionary<long, EdgeId>? edgeMap = null,
        IDictionary<long, NexusId>? nexusMap = null)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(mutations);
        vertexMap ??= new Dictionary<long, VertexId>();
        edgeMap ??= new Dictionary<long, EdgeId>();
        nexusMap ??= new Dictionary<long, NexusId>();

        foreach (var m in mutations)
        {
            switch (m.Kind)
            {
                case LogicalMutationKind.CreateVertex:
                {
                    var newId = tx.CreateVertex(m.TokenName ?? string.Empty);
                    vertexMap[m.VertexId.Value] = newId;
                    break;
                }
                case LogicalMutationKind.DeleteVertex:
                {
                    if (vertexMap.TryGetValue(m.VertexId.Value, out var vertexId))
                        tx.DeleteVertex(vertexId);
                    break;
                }
                case LogicalMutationKind.CreateEdge:
                {
                    if (!vertexMap.TryGetValue(m.VertexId.Value, out var src)) break;
                    if (!vertexMap.TryGetValue(m.TargetVertexId.Value, out var tgt)) break;
                    var newId = tx.CreateEdge(src, tgt, m.TokenName ?? string.Empty);
                    edgeMap[m.EdgeId.Value] = newId;
                    break;
                }
                case LogicalMutationKind.DeleteEdge:
                {
                    if (edgeMap.TryGetValue(m.EdgeId.Value, out var edgeId))
                        tx.DeleteEdge(edgeId);
                    break;
                }
                case LogicalMutationKind.SetVertexProperty:
                {
                    if (!vertexMap.TryGetValue(m.VertexId.Value, out var vertexId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.SetProperty(vertexId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.SetEdgeProperty:
                {
                    if (!edgeMap.TryGetValue(m.EdgeId.Value, out var edgeId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.SetProperty(edgeId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.RemoveVertexProperty:
                {
                    if (vertexMap.TryGetValue(m.VertexId.Value, out var vertexId))
                        tx.RemoveProperty(vertexId, m.PropertyKey ?? string.Empty);
                    break;
                }
                case LogicalMutationKind.RemoveEdgeProperty:
                {
                    if (edgeMap.TryGetValue(m.EdgeId.Value, out var edgeId))
                        tx.RemoveProperty(edgeId, m.PropertyKey ?? string.Empty);
                    break;
                }
                case LogicalMutationKind.AddVertexPropertyValue:
                {
                    if (!vertexMap.TryGetValue(m.VertexId.Value, out var vertexId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.AddPropertyValue(vertexId, m.PropertyKey ?? string.Empty, in value);
                    break;
                }
                case LogicalMutationKind.RemoveVertexPropertyValue:
                {
                    if (!vertexMap.TryGetValue(m.VertexId.Value, out var vertexId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.RemovePropertyValue(vertexId, m.PropertyKey ?? string.Empty, in value);
                    break;
                }
                case LogicalMutationKind.AddEdgePropertyValue:
                {
                    if (!edgeMap.TryGetValue(m.EdgeId.Value, out var edgeId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.AddPropertyValue(edgeId, m.PropertyKey ?? string.Empty, in value);
                    break;
                }
                case LogicalMutationKind.RemoveEdgePropertyValue:
                {
                    if (!edgeMap.TryGetValue(m.EdgeId.Value, out var edgeId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.RemovePropertyValue(edgeId, m.PropertyKey ?? string.Empty, in value);
                    break;
                }
                case LogicalMutationKind.CreateNexus:
                {
                    var source = m.Members;
                    if (source is null || source.Count == 0) break;
                    // メンバーの VertexId をターゲット DB の ID へ再マッピングする。
                    // 依存Vertexが未再生 (マップ欠落) なら、このNexusは再生しない。
                    var remapped = new NexusMember[source.Count];
                    bool complete = true;
                    for (int i = 0; i < source.Count; i++)
                    {
                        if (!vertexMap.TryGetValue(source[i].VertexId.Value, out var mapped))
                        {
                            complete = false;
                            break;
                        }
                        remapped[i] = new NexusMember(source[i].Role, mapped);
                    }
                    if (!complete) break;
                    var newId = tx.CreateNexus(m.TokenName ?? string.Empty, remapped);
                    nexusMap[m.NexusId.Value] = newId;
                    break;
                }
                case LogicalMutationKind.DeleteNexus:
                {
                    if (nexusMap.TryGetValue(m.NexusId.Value, out var heId))
                        tx.DeleteNexus(heId);
                    break;
                }
                case LogicalMutationKind.SetNexusProperty:
                {
                    if (!nexusMap.TryGetValue(m.NexusId.Value, out var heId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.SetProperty(heId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.RemoveNexusProperty:
                {
                    if (nexusMap.TryGetValue(m.NexusId.Value, out var heId))
                        tx.RemoveProperty(heId, m.PropertyKey ?? string.Empty);
                    break;
                }
                case LogicalMutationKind.AddNexusPropertyValue:
                {
                    if (!nexusMap.TryGetValue(m.NexusId.Value, out var heId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.AddPropertyValue(heId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
                case LogicalMutationKind.RemoveNexusPropertyValue:
                {
                    if (!nexusMap.TryGetValue(m.NexusId.Value, out var heId)) break;
                    var value = m.PropertyValue.ToPropertyValue();
                    tx.RemovePropertyValue(heId, m.PropertyKey ?? string.Empty, value);
                    break;
                }
            }
        }
    }
}

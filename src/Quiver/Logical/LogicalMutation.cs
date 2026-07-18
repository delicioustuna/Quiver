using Quiver.Core;

namespace Quiver.Logical;

/// <summary>
/// 1 件のグラフミューテーションを表すセマンティックレコード。
///
/// 論理ミューテーションは書き込み中の <see cref="IWriteTransaction"/> によって、
/// 各公開ミューテーション呼び出しの後に生成され、コミットまでバッファされる。
/// 下層トランザクションが永続化コミットされる (WAL フラッシュ) と、
/// <see cref="ILogicalMutationSink"/> に渡される。
///
/// レコードは自己完結している — ラベル / Edge型 / プロパティキーの名称は
/// トークン ID ではなく文字列で保持するため、まだそれらトークンが未登録のグラフに対しても
/// ストリームを検査・送信・再生できる (トークン ID はソース DB とターゲット DB で異なる)。
/// </summary>
public readonly struct LogicalMutation
{
    /// <summary>ミューテーションの種別。</summary>
    public LogicalMutationKind Kind { get; }

    /// <summary>主要なVertex ID (CreateVertex / DeleteVertex / *VertexProperty / CreateEdge の source)。</summary>
    public VertexId VertexId { get; }

    /// <summary>CreateEdge のターゲットVertex。</summary>
    public VertexId TargetVertexId { get; }

    /// <summary>Edge ID (CreateEdge の戻り値 / DeleteEdge / SetEdgeProperty)。</summary>
    public EdgeId EdgeId { get; }

    /// <summary>Nexus ID (CreateNexus の戻り値 / DeleteNexus / *NexusProperty)。</summary>
    public NexusId NexusId { get; }

    /// <summary><see cref="LogicalMutationKind.CreateVertex"/> ではラベル名、<see cref="LogicalMutationKind.CreateEdge"/> ではEdge型名、<see cref="LogicalMutationKind.CreateNexus"/> ではNexus型名。</summary>
    public string? TokenName { get; }

    /// <summary>*Property ミューテーションのプロパティキー名。</summary>
    public string? PropertyKey { get; }

    /// <summary><see cref="LogicalMutationKind.SetVertexProperty"/> / <see cref="LogicalMutationKind.SetEdgeProperty"/> / <see cref="LogicalMutationKind.SetNexusProperty"/> 等のプロパティ値。</summary>
    public LogicalPropertyValue PropertyValue { get; }

    /// <summary>
    /// <see cref="LogicalMutationKind.CreateNexus"/> のメンバー列 (ロール名 + ソース側 <see cref="VertexId"/>)。
    /// 再生時に各メンバーの <see cref="VertexId"/> をターゲット DB の ID へ再マッピングする。
    /// 他の種別では <c>null</c>。
    /// </summary>
    public IReadOnlyList<NexusMember>? Members { get; }

    private LogicalMutation(
        LogicalMutationKind kind,
        VertexId vertexId = default,
        VertexId targetVertexId = default,
        EdgeId edgeId = default,
        NexusId nexusId = default,
        string? tokenName = null,
        string? propertyKey = null,
        LogicalPropertyValue propertyValue = default,
        IReadOnlyList<NexusMember>? members = null)
    {
        Kind = kind;
        VertexId = vertexId;
        TargetVertexId = targetVertexId;
        EdgeId = edgeId;
        NexusId = nexusId;
        TokenName = tokenName;
        PropertyKey = propertyKey;
        PropertyValue = propertyValue;
        Members = members;
    }

    /// <summary>Vertex作成のミューテーションレコードを生成する。</summary>
    public static LogicalMutation CreateVertex(VertexId vertexId, string label)
        => new(LogicalMutationKind.CreateVertex, vertexId: vertexId, tokenName: label);

    /// <summary>Vertex削除のミューテーションレコードを生成する。</summary>
    public static LogicalMutation DeleteVertex(VertexId vertexId)
        => new(LogicalMutationKind.DeleteVertex, vertexId: vertexId);

    /// <summary>Edge作成のミューテーションレコードを生成する。</summary>
    public static LogicalMutation CreateEdge(
        EdgeId edgeId, VertexId source, VertexId target, string type)
        => new(LogicalMutationKind.CreateEdge,
            vertexId: source, targetVertexId: target,
            edgeId: edgeId, tokenName: type);

    /// <summary>Edge削除のミューテーションレコードを生成する。</summary>
    public static LogicalMutation DeleteEdge(EdgeId edgeId)
        => new(LogicalMutationKind.DeleteEdge, edgeId: edgeId);

    /// <summary>Vertexプロパティ設定のミューテーションレコードを生成する。</summary>
    public static LogicalMutation SetVertexProperty(VertexId vertexId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.SetVertexProperty, vertexId: vertexId, propertyKey: key, propertyValue: value);

    /// <summary>Edgeプロパティ設定のミューテーションレコードを生成する。</summary>
    public static LogicalMutation SetEdgeProperty(EdgeId edgeId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.SetEdgeProperty,
            edgeId: edgeId, propertyKey: key, propertyValue: value);

    /// <summary>Vertexプロパティ削除のミューテーションレコードを生成する。</summary>
    public static LogicalMutation RemoveVertexProperty(VertexId vertexId, string key)
        => new(LogicalMutationKind.RemoveVertexProperty, vertexId: vertexId, propertyKey: key);

    /// <summary>
    /// Nexus作成のミューテーションレコードを生成する。
    /// <paramref name="members"/> はロール名とソース側 <see cref="VertexId"/> を保持し、再生時に再マッピングされる。
    /// </summary>
    public static LogicalMutation CreateNexus(
        NexusId nexusId, string type, IReadOnlyList<NexusMember> members)
        => new(LogicalMutationKind.CreateNexus,
            nexusId: nexusId, tokenName: type, members: members);

    /// <summary>Nexus削除のミューテーションレコードを生成する。</summary>
    public static LogicalMutation DeleteNexus(NexusId nexusId)
        => new(LogicalMutationKind.DeleteNexus, nexusId: nexusId);

    /// <summary>Nexusプロパティ設定のミューテーションレコードを生成する。</summary>
    public static LogicalMutation SetNexusProperty(NexusId nexusId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.SetNexusProperty,
            nexusId: nexusId, propertyKey: key, propertyValue: value);

    /// <summary>Nexusプロパティ削除のミューテーションレコードを生成する。</summary>
    public static LogicalMutation RemoveNexusProperty(NexusId nexusId, string key)
        => new(LogicalMutationKind.RemoveNexusProperty, nexusId: nexusId, propertyKey: key);

    /// <summary>Nexusのマルチバリュープロパティへの値追加ミューテーションレコードを生成する。</summary>
    public static LogicalMutation AddNexusPropertyValue(NexusId nexusId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.AddNexusPropertyValue,
            nexusId: nexusId, propertyKey: key, propertyValue: value);

    /// <summary>Nexusのマルチバリュープロパティからの値除去ミューテーションレコードを生成する。</summary>
    public static LogicalMutation RemoveNexusPropertyValue(NexusId nexusId, string key, in LogicalPropertyValue value)
        => new(LogicalMutationKind.RemoveNexusPropertyValue,
            nexusId: nexusId, propertyKey: key, propertyValue: value);
}

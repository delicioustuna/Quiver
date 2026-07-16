namespace Quiver.Logical;

/// <summary>
/// <see cref="LogicalMutation"/> の判別子。
/// <see cref="IGraphTransaction"/> の公開ミューテーション API と対応する。
/// </summary>
public enum LogicalMutationKind : byte
{
    /// <summary>Vertex作成。</summary>
    CreateVertex = 1,
    /// <summary>Vertex削除。</summary>
    DeleteVertex = 2,
    /// <summary>Edge作成。</summary>
    CreateEdge = 3,
    /// <summary>Edge削除。</summary>
    DeleteEdge = 4,
    /// <summary>Vertexプロパティの設定。</summary>
    SetVertexProperty = 5,
    /// <summary>Edgeプロパティの設定。</summary>
    SetEdgeProperty = 6,
    /// <summary>Vertexプロパティの削除。</summary>
    RemoveVertexProperty = 7,
    /// <summary>Nexus作成。</summary>
    CreateNexus = 8,
    /// <summary>Nexus削除。</summary>
    DeleteNexus = 9,
    /// <summary>Nexusプロパティの設定 (Single cardinality)。</summary>
    SetNexusProperty = 10,
    /// <summary>Nexusプロパティの削除 (Single cardinality)。</summary>
    RemoveNexusProperty = 11,
    /// <summary>Nexusのマルチバリュープロパティへの値追加 (Set cardinality)。</summary>
    AddNexusPropertyValue = 12,
    /// <summary>Nexusのマルチバリュープロパティからの値除去 (Set cardinality)。</summary>
    RemoveNexusPropertyValue = 13,
}

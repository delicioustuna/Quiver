namespace Yatagarasu.SourceGen;

internal sealed class GraphEdgeModel
{
    public string Namespace { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string EdgeType { get; set; } = "";
    public bool IsPublic { get; set; }

    /// <summary>始点Vertex型の完全修飾名 (<c>global::Ns.Person</c>)。</summary>
    public string SourceFqn { get; set; } = "";

    /// <summary>終点Vertex型の完全修飾名 (<c>global::Ns.Company</c>)。</summary>
    public string TargetFqn { get; set; } = "";

    public List<PropertyModel> Properties { get; } = new();
}

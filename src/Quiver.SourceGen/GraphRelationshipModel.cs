namespace Quiver.SourceGen;

internal sealed class GraphRelationshipModel
{
    public string Namespace { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string RelType { get; set; } = "";

    /// <summary>始点ノード型の完全修飾名 (<c>global::Ns.Person</c>)。</summary>
    public string SourceFqn { get; set; } = "";

    /// <summary>終点ノード型の完全修飾名 (<c>global::Ns.Company</c>)。</summary>
    public string TargetFqn { get; set; } = "";

    public List<PropertyModel> Properties { get; } = new();
}

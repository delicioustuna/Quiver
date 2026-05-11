namespace Quiver.SourceGen;

internal sealed class GraphRelationshipModel
{
    public string Namespace { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string RelType { get; set; } = "";
    public List<PropertyModel> Properties { get; } = new();
}

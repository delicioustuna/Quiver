namespace Quiver.SourceGen;

internal sealed class GraphNodeModel
{
    public string Namespace { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Label { get; set; } = "";
    public List<PropertyModel> Properties { get; } = new();
}

internal sealed class PropertyModel
{
    public string PropertyName { get; set; } = "";
    public string GraphKey { get; set; } = "";
    public string CSharpType { get; set; } = "";
    public string? IndexName { get; set; }
}

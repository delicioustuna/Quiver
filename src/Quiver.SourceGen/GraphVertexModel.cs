namespace Quiver.SourceGen;

internal sealed class GraphVertexModel
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
    /// <summary>スカラー型名。<see cref="IsMultiValued"/> が <c>true</c> のとき要素型を保持する。</summary>
    public string CSharpType { get; set; } = "";
    public string? IndexName { get; set; }
    /// <summary><c>List&lt;T&gt;</c> 型プロパティで Set cardinality の多値 CRUD を emit する。</summary>
    public bool IsMultiValued { get; set; }
}

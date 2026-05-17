using Quiver.Client;

namespace Quiver.Samples.SourceGen;

// [GraphNode] / [GraphIndexed] / [GraphProperty] を付けると、Roslyn SourceGenerator が
// Insert / InsertIndexed / Load / Update / Delete / FindByName を自動生成する。
[GraphNode("Person")]
public partial class Person
{
    [GraphIndexed("idx_person_name")]
    [GraphProperty]
    public string Name { get; set; } = "";

    [GraphProperty]
    public int Age { get; set; }
}

[GraphRelationship("KNOWS")]
public partial class Knows
{
    [GraphProperty]
    public string Since { get; set; } = "";
}

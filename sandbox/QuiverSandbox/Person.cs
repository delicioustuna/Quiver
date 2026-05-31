using Quiver.Api;

[GraphNode("Person")]
public partial class Person
{
    [GraphIndexed("idx_person_name")]
    [GraphProperty]
    public string Name { get; set; } = "";

    [GraphProperty]
    public int Age { get; set; }
}

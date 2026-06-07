using Quiver.Api;

namespace Quiver.Samples.SourceGen;

// [Node] / [Indexed] / [Property] を付けると、Roslyn SourceGenerator が
// Insert / InsertIndexed / Load / Update / Delete / FindByName を自動生成する。
[Node("Person")]
public partial class Person
{
    [Indexed("idx_person_name")]
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

[Relationship<Person, Person>("KNOWS")]
public partial class Knows
{
    [Property]
    public string Since { get; set; } = "";
}

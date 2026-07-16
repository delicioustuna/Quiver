using Quiver.Api;

namespace Quiver.Samples.QueryPatterns;

[Vertex("Person")]
public partial class Person
{
    [Property] public string Name { get; set; } = "";
    [Property] public int Age { get; set; }
}

[Vertex("Tool")]
public partial class Tool
{
    [Property] public string Name { get; set; } = "";
}

[Edge<Person, Person>("KNOWS")]
public partial class Knows
{
    [Property] public string Since { get; set; } = "";
}

[Edge<Person, Tool>("USE")]
public partial class Use
{
    [Property] public string Note { get; set; } = "";
}

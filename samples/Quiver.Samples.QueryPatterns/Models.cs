using Quiver.Api;

namespace Quiver.Samples.QueryPatterns;

[Vertex("Person")]
internal partial class Person
{
    [Property] public string Name { get; set; } = "";
    [Property] public int Age { get; set; }
}

[Vertex("Tool")]
internal partial class Tool
{
    [Property] public string Name { get; set; } = "";
}

[Edge<Person, Person>("KNOWS")]
internal partial class Knows
{
    [Property] public string Since { get; set; } = "";
}

[Edge<Person, Tool>("USE")]
internal partial class Use
{
    [Property] public string Note { get; set; } = "";
}

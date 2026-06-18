using Quiver.Api;

namespace Quiver.Samples.QueryPatterns;

[Node("Person")]
public partial class Person
{
    [Property] public string Name { get; set; } = "";
    [Property] public int Age { get; set; }
}

[Node("Tool")]
public partial class Tool
{
    [Property] public string Name { get; set; } = "";
}

[Relationship<Person, Person>("KNOWS")]
public partial class Knows
{
    [Property] public string Since { get; set; } = "";
}

[Relationship<Person, Tool>("USE")]
public partial class Use
{
    [Property] public string Note { get; set; } = "";
}

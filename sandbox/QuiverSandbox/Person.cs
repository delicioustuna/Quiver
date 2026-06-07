using Quiver.Api;

[Node("Person")]
public partial class Person
{
    [Indexed("idx_person_name")]
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }
}

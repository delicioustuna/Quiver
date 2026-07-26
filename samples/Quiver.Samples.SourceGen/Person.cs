using Quiver.Api;

namespace Quiver.Samples.SourceGen;

// [Vertex] / [Indexed] / [Property] を付けると、Roslyn SourceGenerator が
// Insert / InsertIndexed / Load / Update / Delete / FindByName を自動生成する。
[Vertex("Person")]
public partial class Person
{
    [Indexed("idx_person_name")]
    [Property]
    public string Name { get; set; } = "";

    [Property]
    public int Age { get; set; }

    // 内部的には float は Double に拡張して格納・型付きクエリで範囲比較できる。
    [Property]
    public float Height { get; set; }

    // 内部的には DateTime は UTC Ticks に正準化して格納 (TimeZone はマシン非依存)。
    [Property]
    public DateTime CreatedAt { get; set; }

    [Property]
    public List<string> Tags { get; set; } = [];
}

[Edge<Person, Person>("KNOWS")]
public partial class Knows
{
    [Property]
    public string Since { get; set; } = "";
}

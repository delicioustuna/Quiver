using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Yatagarasu.Hosting;
using Yatagarasu.OpenTelemetry;
using Yatagarasu.Rag;

string path = Path.Combine(AppContext.BaseDirectory, "consumer.yata");
using (var graph = GraphWorkspace.Open(path))
{
    graph.Write(write =>
    {
        var people = write.Set<Person>();
        write.Add(people, new Person { Name = "Alice", Age = 30 });
    });
}
using (var graph = GraphWorkspace.Open(path))
{
    var people = graph.Read(read => read.Raw.Query.Vertices<Person>().ToList());
    if (people.Count != 1 || people[0].Name != "Alice" || people[0].Age != 30)
        throw new InvalidOperationException("Packaged typed CRUD/reopen failed.");
}
if (Chunker.Chunk([]).Count != 0) throw new InvalidOperationException("RAG package failed.");
var services = new ServiceCollection();
services.AddYatagarasu(options => options.DataDirectory = Path.Combine(AppContext.BaseDirectory, "hosted.yata"));
if (!services.Any(service => service.ServiceType == typeof(GraphStore)))
    throw new InvalidOperationException("Hosting registration failed.");
using var tracer = Sdk.CreateTracerProviderBuilder().AddYatagarasuInstrumentation().Build();
Console.WriteLine("Package consumer passed: typed source generation, CRUD/reopen, RAG, Hosting, OpenTelemetry.");

[Vertex]
public partial class Person
{
    [Property]
    public string Name { get; set; } = "";
    [Property]
    public int Age { get; set; }
}

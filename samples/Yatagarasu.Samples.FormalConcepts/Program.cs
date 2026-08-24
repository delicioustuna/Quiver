using Yatagarasu;
using Yatagarasu.Core;

using var database = YatagarasuDatabase.CreateInMemory();
var names = new Dictionary<EntityRef, string>();
using (IWriteTransaction write = database.BeginWriteTransaction())
{
    VertexId graph = AddObject(write, names, "graph");
    VertexId vector = AddObject(write, names, "vector");
    VertexId fullText = AddObject(write, names, "full-text");
    AddAttribute(write, names, "searchable", graph, vector, fullText);
    AddAttribute(write, names, "structured", graph, vector);
    AddAttribute(write, names, "lexical", graph, fullText);
    write.Commit();
}

using IReadTransaction read = database.BeginReadTransaction();
var options = new FormalConceptOptions
{
    Strategy = FormalConceptEnumerationStrategy.NextClosure,
    PageSize = 1,
    MaxResults = 100,
    MaxClosureEvaluations = 10_000,
    MaxObjects = 100,
    MaxAttributes = 100,
    MaxIncidences = 1_000,
    TimeLimit = TimeSpan.FromSeconds(1),
};
FormalConceptContinuation? continuation = null;
do
{
    FormalConceptResult page = read.EnumerateFormalConcepts(
        "Capability", "object", options, continuation);
    foreach (FormalConcept concept in page.Concepts)
    {
        Console.WriteLine(
            $"objects=[{string.Join(", ", concept.Extent.Select(id => names[id]))}] " +
            $"attributes=[{string.Join(", ", concept.Intent.Select(id => names[id]))}]");
    }
    continuation = page.Continuation;
}
while (continuation is not null); // 継続はこの read transaction の同じ snapshot にだけ束縛される。

static VertexId AddObject(IWriteTransaction write, Dictionary<EntityRef, string> names, string name)
{
    VertexId id = write.CreateVertex("Object");
    names[EntityRef.From(id)] = name;
    return id;
}

static void AddAttribute(
    IWriteTransaction write,
    Dictionary<EntityRef, string> names,
    string name,
    params VertexId[] objects)
{
    NexusId id = write.CreateNexus(
        "Capability",
        objects.Select(obj => new NexusMember("object", obj)).ToArray());
    names[EntityRef.From(id)] = name;
}

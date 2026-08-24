using Yatagarasu;
using Yatagarasu.Core;

// 複数の要件を満たす最小のサービス集合を、ロール付きNexusから同一snapshot上で求める。
using var database = YatagarasuDatabase.CreateInMemory();
using (var write = database.BeginWriteTransaction())
{
    VertexId identity = Service(write, "Identity");
    VertexId audit = Service(write, "Audit");
    VertexId archive = Service(write, "Archive");
    VertexId search = Service(write, "Search");
    VertexId scope = write.CreateVertex("Deployment");

    Requirement(write, scope, "Authentication", identity, audit);
    Requirement(write, scope, "Traceability", audit, archive);
    Requirement(write, scope, "Retention", archive, search);
    Requirement(write, scope, "Discovery", search, identity);
    write.Commit();
}

using var read = database.BeginReadTransaction();
MinimumHittingSetResult result = read.FindMinimumHittingSet(
    "Requirement",
    "provider",
    new MinimumHittingSetOptions
    {
        MaxNodes = 100_000,
        TimeLimit = TimeSpan.FromSeconds(1),
    });

Console.WriteLine(
    $"solution={result.Solution.Count} lower={result.LowerBound} upper={result.UpperBound} " +
    $"optimal={result.IsOptimal} reason={result.TerminationReason}");
foreach (VertexId service in result.Solution)
    Console.WriteLine($"- {System.Text.Encoding.UTF8.GetString(read.GetProperty(service, "name").Utf8StringValue)}");

static VertexId Service(IWriteTransaction write, string name)
{
    VertexId service = write.CreateVertex("Service");
    write.SetProperty(service, "name", Yatagarasu.Storage.Records.PropertyValue.FromString(name));
    return service;
}

static void Requirement(
    IWriteTransaction write,
    VertexId scope,
    string name,
    params VertexId[] providers)
{
    NexusMember[] members = providers.Select(provider => new NexusMember("provider", provider))
        .Append(new("scope", scope))
        .ToArray();
    NexusId requirement = write.CreateNexus("Requirement", members);
    write.SetProperty(requirement, "name", Yatagarasu.Storage.Records.PropertyValue.FromString(name));
}

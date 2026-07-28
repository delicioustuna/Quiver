using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;

// 同じ依存グラフを、到達可能性、最小コスト、最大信頼度、経路多重度という異なる注釈で読む。
using var database = QuiverDatabase.CreateInMemory();
VertexId origin;
VertexId destination;
using (IWriteTransaction write = database.BeginWriteTransaction())
{
    origin = Component(write, "Origin");
    VertexId cache = Component(write, "Cache");
    VertexId index = Component(write, "Index");
    destination = Component(write, "Destination");

    Dependency(write, origin, cache, cost: 2.0, reliability: 0.90);
    Dependency(write, origin, index, cost: 1.0, reliability: 0.70);
    Dependency(write, cache, destination, cost: 2.0, reliability: 0.80);
    Dependency(write, cache, index, cost: 0.5, reliability: 0.90);
    Dependency(write, index, destination, cost: 4.0, reliability: 0.95);
    write.Commit();
}

using IReadTransaction read = database.BeginReadTransaction();
var options = new GraphAnnotationOptions
{
    EdgeType = "DependsOn",
    MaxVertices = 100,
    MaxEdges = 1_000,
    MaxResults = 100,
    MaxRelaxations = 10_000,
    MaxAnnotationUpdates = 10_000,
    TimeLimit = TimeSpan.FromSeconds(1),
};

GraphAnnotationResult<bool> reachable = read.EvaluateReachability(
    origin,
    GraphAnnotationPolicy.BreadthFirst,
    options);
GraphAnnotationResult<double> costs = read.EvaluateTropical(
    origin,
    GraphAnnotationPolicy.LabelSetting,
    static (transaction, edge) => transaction.GetProperty(edge, "cost").DoubleValue,
    options);
GraphAnnotationResult<double> reliability = read.EvaluateViterbi(
    origin,
    GraphAnnotationPolicy.AcyclicDynamicProgramming,
    static (transaction, edge) => transaction.GetProperty(edge, "reliability").DoubleValue,
    options);
GraphAnnotationResult<long> multiplicity = read.EvaluatePathMultiplicity(
    origin,
    GraphAnnotationPolicy.BoundedWorklist,
    options);

Console.WriteLine($"reachable={reachable.Annotations.Count} exact={reachable.IsExact}");
Console.WriteLine($"destination-cost={Value(costs, destination):F2}");
Console.WriteLine($"destination-reliability={Value(reliability, destination):P2}");
Console.WriteLine($"destination-paths={Value(multiplicity, destination)}");

static VertexId Component(IWriteTransaction write, string name)
{
    VertexId vertex = write.CreateVertex("Component");
    write.SetProperty(vertex, "name", PropertyValue.FromString(name));
    return vertex;
}

static void Dependency(
    IWriteTransaction write,
    VertexId source,
    VertexId target,
    double cost,
    double reliability)
{
    EdgeId edge = write.CreateEdge(source, target, "DependsOn");
    write.SetProperty(edge, "cost", PropertyValue.FromDouble(cost));
    write.SetProperty(edge, "reliability", PropertyValue.FromDouble(reliability));
}

static T Value<T>(GraphAnnotationResult<T> result, VertexId vertex)
    => result.Annotations.Single(annotation => annotation.VertexId == vertex).Value;

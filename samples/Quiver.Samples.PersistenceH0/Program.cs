using Quiver;
using Quiver.Core;

using var database = QuiverDatabase.CreateInMemory();
using (IWriteTransaction schema = database.BeginWriteTransaction())
{
    schema.EditSchema.CreateIndex(new VectorIndexDefinition(
        "positions",
        new PropertyTarget(PropertyOwnerKind.Vertex, "position", "Observation"),
        Dimensions: 2,
        Metric: DistanceMetric.Euclidean));
    schema.Commit();
}

float[][] observations =
[
    [0, 0], [0, 1], [1, 0],
    [20, 0], [20, 1], [21, 0],
    [40, 0], [40, 1], [41, 0],
];
using (IWriteTransaction write = database.BeginWriteTransaction())
{
    foreach (float[] position in observations)
    {
        VertexId observation = write.CreateVertex("Observation");
        write.SetVectorProperty(EntityRef.From(observation), "position", position);
    }
    write.Commit();
}

using IReadTransaction read = database.BeginReadTransaction();
PersistenceH0Result barcode = PersistenceH0Algorithms.Compute(
    read,
    "positions",
    new PersistenceH0Options
    {
        Filtration = PersistenceH0Filtration.Complete,
        MaxPoints = 100,
        MaxEdges = 10_000,
        MaxDistanceEvaluations = 10_000,
        MaxResults = 100,
    });
PersistenceClusterEstimate estimate = PersistenceH0Algorithms.EstimateClusters(barcode);

Console.WriteLine(
    $"points={barcode.PointCount} finiteIntervals={barcode.Intervals.Count(interval => interval.Death.HasValue)} " +
    $"exact={barcode.IsExact} clusters={estimate.ClusterCount} separationScale={estimate.SeparationScale:F3}");

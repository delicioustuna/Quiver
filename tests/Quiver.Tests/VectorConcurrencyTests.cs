using System.Collections.Concurrent;
using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class VectorConcurrencyTests : IDisposable
{
    private const string IndexName = "concurrent_vectors";
    private const int Dimensions = 32;
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "quiver_vector_concurrency_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Concurrent_knn_readers_and_writer_remain_consistent()
    {
        using var db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName,
            EntityKind.Node,
            db.Schema.GetOrCreatePropertyKey("embedding"),
            Dimensions,
            DistanceMetric.Cosine,
            "concurrency test"));

        var random = new Random(42);
        var vector = new float[Dimensions];
        NodeId updated = default;
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 200; i++)
            {
                var node = tx.CreateNode("Doc");
                if (i == 0) updated = node;
                Fill(random, vector);
                tx.SetVector(EntityKind.Node, node.Value, IndexName, vector);
            }
            tx.Commit();
        }

        var query = new float[Dimensions];
        Fill(random, query);
        var errors = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    int count = 0;
                    using var cursor = db.Vectors.KnnSearch(IndexName, query, 10);
                    while (cursor.MoveNext()) count++;
                    count.Should().Be(10);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                var local = new Random(84);
                var replacement = new float[Dimensions];
                for (int i = 0; i < 25; i++)
                {
                    Fill(local, replacement);
                    db.Vectors.SetVector(
                        EntityKind.Node, updated.Value, IndexName, replacement);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        start.Set();
        await Task.WhenAll(readers.Append(writer));
        errors.Should().BeEmpty();
    }

    private static void Fill(Random random, Span<float> vector)
    {
        for (int i = 0; i < vector.Length; i++)
            vector[i] = (float)(random.NextDouble() * 2 - 1);
    }
}

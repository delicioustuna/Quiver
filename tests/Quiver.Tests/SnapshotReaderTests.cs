using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class SnapshotReaderTests : IDisposable
{
    private readonly string _directory;
    private readonly QuiverDatabase _database;

    public SnapshotReaderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "quiver_snapshot_readers_" + Guid.NewGuid().ToString("N"));
        _database = QuiverDatabase.Open(Path.Combine(_directory, "graph.quiver"));
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Thirty_two_readers_start_during_writer_and_keep_their_start_snapshot()
    {
        using var writer = _database.BeginWriteTransaction();
        VertexId created = writer.CreateVertex("created");

        IReadTransaction[] readers = Enumerable.Range(0, 32)
            .Select(_ => _database.BeginReadTransaction())
            .ToArray();
        try
        {
            readers.Should().OnlyContain(reader => !reader.VertexExists(created));
            writer.Commit();
            readers.Should().OnlyContain(reader => !reader.VertexExists(created));

            using var fresh = _database.BeginReadTransaction();
            fresh.VertexExists(created).Should().BeTrue();
        }
        finally
        {
            foreach (IReadTransaction reader in readers) reader.Dispose();
        }
    }

    [Fact]
    public void Long_reader_does_not_drift_across_update_and_delete_and_does_not_block_commit()
    {
        VertexId vertex;
        using (var seed = _database.BeginWriteTransaction())
        {
            vertex = seed.CreateVertex("item");
            seed.SetProperty(vertex, "value", PropertyValue.FromInt32(1));
            seed.Commit();
        }

        using var oldReader = _database.BeginReadTransaction();
        using (var writer = _database.BeginWriteTransaction())
        {
            writer.SetProperty(vertex, "value", PropertyValue.FromInt32(2));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            writer.Commit();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }

        oldReader.GetProperty(vertex, "value").Int32Value.Should().Be(1);
        using (var fresh = _database.BeginReadTransaction())
            fresh.GetProperty(vertex, "value").Int32Value.Should().Be(2);

        using (var delete = _database.BeginWriteTransaction())
        {
            delete.DeleteVertex(vertex);
            delete.Commit();
        }
        oldReader.VertexExists(vertex).Should().BeTrue();
        using var afterDelete = _database.BeginReadTransaction();
        afterDelete.VertexExists(vertex).Should().BeFalse();
    }

    [Fact]
    public void Read_transaction_surface_exposes_no_mutation_methods()
    {
        using var reader = _database.BeginReadTransaction();
        typeof(IReadTransaction).GetMethod(nameof(IWriteTransaction.CreateVertex))
            .Should().BeNull();
        _database.Schema.ListLabels().Should().NotContain("must-not-exist");
    }
}

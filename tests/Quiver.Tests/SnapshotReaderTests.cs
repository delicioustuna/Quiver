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
        using var writer = _database.BeginTransaction();
        VertexId created = writer.CreateVertex("created");

        IGraphTransaction[] readers = Enumerable.Range(0, 32)
            .Select(_ => _database.BeginReadOnlyTransaction())
            .ToArray();
        try
        {
            readers.Should().OnlyContain(reader => !reader.VertexExists(created));
            writer.Commit();
            readers.Should().OnlyContain(reader => !reader.VertexExists(created));

            using var fresh = _database.BeginReadOnlyTransaction();
            fresh.VertexExists(created).Should().BeTrue();
        }
        finally
        {
            foreach (IGraphTransaction reader in readers) reader.Dispose();
        }
    }

    [Fact]
    public void Long_reader_does_not_drift_across_update_and_delete_and_does_not_block_commit()
    {
        VertexId vertex;
        using (var seed = _database.BeginTransaction())
        {
            vertex = seed.CreateVertex("item");
            seed.SetProperty(vertex, "value", PropertyValue.FromInt32(1));
            seed.Commit();
        }

        using var oldReader = _database.BeginReadOnlyTransaction();
        using (var writer = _database.BeginTransaction())
        {
            writer.SetProperty(vertex, "value", PropertyValue.FromInt32(2));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            writer.Commit();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }

        oldReader.GetProperty(vertex, "value").Int32Value.Should().Be(1);
        using (var fresh = _database.BeginReadOnlyTransaction())
            fresh.GetProperty(vertex, "value").Int32Value.Should().Be(2);

        using (var delete = _database.BeginTransaction())
        {
            delete.DeleteVertex(vertex);
            delete.Commit();
        }
        oldReader.VertexExists(vertex).Should().BeTrue();
        using var afterDelete = _database.BeginReadOnlyTransaction();
        afterDelete.VertexExists(vertex).Should().BeFalse();
    }

    [Fact]
    public void Read_only_mutation_is_rejected_before_token_or_record_changes()
    {
        using var reader = _database.BeginReadOnlyTransaction();
        Action mutate = () => reader.CreateVertex("must-not-exist");
        mutate.Should().Throw<TransactionException>();
        _database.Schema.ListLabels().Should().NotContain("must-not-exist");
    }
}

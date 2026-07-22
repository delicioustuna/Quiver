using FluentAssertions;
using Quiver.Backend.Tests.Faults;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

public sealed class WriterLeaseDurabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "quiver_writer_lease_durability_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
        => TestTempCleanup.DeleteDirectoryRobust(_directory);

    [Fact]
    public void Oversized_transaction_aborts_and_releases_the_writer_lease()
    {
        string path = Path.Combine(_directory, "graph.quiver");
        var options = new QuiverDatabaseOptions
        {
            BufferPoolSize = 64L * PagedFile.PageSizeConst,
            CheckpointThresholdBytes = 0,
        };
        var factory = new BinaryGraphStorageBackendFactory();
        IGraphStorageBackend? backend = factory.Open(path, options);

        TransactionTooLargeException? overflow = null;
        using (IWriteTransaction transaction = backend.BeginWriteTransaction())
        {
            try
            {
                string payload = new('x', 7_000);
                for (int index = 0; index < 1_000; index++)
                {
                    VertexId vertex = transaction.CreateVertex("Large");
                    transaction.SetProperty(
                        vertex,
                        "payload",
                        PropertyValue.FromString(payload + index));
                }
            }
            catch (TransactionTooLargeException exception)
            {
                overflow = exception;
            }

            overflow.Should().NotBeNull();
            transaction.State.Should().Be(TransactionState.Aborted);
            overflow!.BufferPoolPageCapacity.Should().Be(64);
        }

        VertexId committed;
        using (IWriteTransaction next = backend.BeginWriteTransaction())
        {
            committed = next.CreateVertex("AfterOverflow");
            next.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);

        using IGraphStorageBackend reopened = factory.Open(path, options);
        using IReadTransaction read = reopened.BeginReadTransaction();
        read.VertexExists(committed).Should().BeTrue();
    }
}

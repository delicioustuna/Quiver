using FluentAssertions;
using Yatagarasu.Backend.Tests.Faults;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Backend.Tests;

public sealed class VectorPayloadAtomicityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "yatagarasu_vector_payload_atomicity_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
        => TestTempCleanup.DeleteDirectoryRobust(_directory);

    [Fact]
    public void Kill_preserves_only_committed_payload_reference_and_index_entry()
    {
        string path = Path.Combine(_directory, "payload.yata");
        var options = new YatagarasuDatabaseOptions
        {
            CheckpointThresholdBytes = 0,
        };
        var factory = new BinaryGraphStorageBackendFactory();
        IGraphStorageBackend? backend = factory.Open(path, options);

        float[] expected = Enumerable.Range(0, 768)
            .Select(index => index / 10.0f)
            .ToArray();
        VertexId committed;
        using (IWriteTransaction transaction = backend.BeginWriteTransaction())
        {
            transaction.EditSchema.CreateIndex(new VectorIndexDefinition(
                "embedding_idx",
                new PropertyTarget(
                    PropertyOwnerKind.Vertex,
                    "embedding",
                    "CommittedPayload"),
                expected.Length));
            committed = transaction.CreateVertex("CommittedPayload");
            transaction.SetVectorProperty(
                EntityRef.From(committed),
                "embedding",
                expected);
            transaction.Commit();
        }

        IWriteTransaction? loser = backend.BeginWriteTransaction();
        VertexId uncommitted = loser.CreateVertex("CommittedPayload");
        loser.SetVectorProperty(
            EntityRef.From(uncommitted),
            "embedding",
            expected.Select(value => -value).ToArray());

        KillProcessSimulator.SimulateKill(ref backend);
        loser = null;

        using IGraphStorageBackend reopened = factory.Open(path, options);
        using IReadTransaction read = reopened.BeginReadTransaction();
        var buffer = new float[expected.Length];
        read.TryGetVectorProperty(
                EntityRef.From(committed),
                "embedding",
                buffer)
            .Should().BeTrue();
        buffer.Should().Equal(expected);
        read.VertexExists(uncommitted).Should().BeFalse();

        using VectorSearchCursor hits = read.KnnSearch(
            "embedding_idx",
            expected,
            10);
        hits.MoveNext().Should().BeTrue();
        hits.Current.Owner.Should().Be(EntityRef.From(committed));
        hits.MoveNext().Should().BeFalse();
    }
}

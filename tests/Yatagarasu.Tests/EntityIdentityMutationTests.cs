using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Logical;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class EntityIdentityMutationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "yatagarasu_identity_mutation_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Stale_targets_are_rejected_before_tokens_or_records_are_mutated()
    {
        var sink = new InMemoryLogicalMutationSink();
        using var database = YatagarasuDatabase.Open(Path.Combine(_directory, "graph.yata"),
            new YatagarasuDatabaseOptions { LogicalMutationSink = sink });
        VertexId source;
        VertexId target;
        EdgeId edge;
        NexusId nexus;
        using (var seed = database.BeginWriteTransaction())
        {
            source = seed.CreateVertex("Entity");
            target = seed.CreateVertex("Entity");
            edge = seed.CreateEdge(source, target, "LINK");
            nexus = seed.CreateNexus("Fact",
            [
                new NexusMember("Subject", source),
                new NexusMember("Object", target),
            ]);
            seed.Commit();
        }

        sink.Clear();
        VertexId staleVertex = VertexId.Create(source.Sequence, source.Generation + 1);
        EdgeId staleEdge = EdgeId.Create(edge.Sequence, edge.Generation + 1);
        NexusId staleNexus = NexusId.Create(nexus.Sequence, nexus.Generation + 1);

        using (var write = database.BeginWriteTransaction())
        {
            Action setVertex = () =>
            {
                PropertyValue value = PropertyValue.FromString("value");
                write.SetProperty(staleVertex, "unused-property", in value);
            };
            Action setEdge = () =>
            {
                PropertyValue value = PropertyValue.FromString("value");
                write.SetProperty(staleEdge, "unused-property", in value);
            };
            Action setNexus = () =>
            {
                PropertyValue value = PropertyValue.FromString("value");
                write.SetProperty(staleNexus, "unused-property", in value);
            };
            Action addValue = () =>
            {
                PropertyValue value = PropertyValue.FromString("value");
                write.AddPropertyValue(staleVertex, "unused-set", in value);
            };
            Action createEdge = () => write.CreateEdge(staleVertex, target, "UNUSED_EDGE_TYPE");
            Action createNexus = () => write.CreateNexus("UNUSED_NEXUS_TYPE",
            [
                new NexusMember("UnusedFirstRole", target),
                new NexusMember("UnusedStaleRole", staleVertex),
            ]);

            setVertex.Should().Throw<KeyNotFoundException>();
            setEdge.Should().Throw<KeyNotFoundException>();
            setNexus.Should().Throw<KeyNotFoundException>();
            addValue.Should().Throw<KeyNotFoundException>();
            createEdge.Should().Throw<KeyNotFoundException>();
            createNexus.Should().Throw<KeyNotFoundException>();
            ((Action)(() => write.RemoveProperty(staleVertex, "unused-remove")))
                .Should().Throw<KeyNotFoundException>();
            ((Action)(() => write.DeleteVertex(staleVertex)))
                .Should().Throw<KeyNotFoundException>();
            ((Action)(() => write.DeleteEdge(staleEdge)))
                .Should().Throw<KeyNotFoundException>();
            ((Action)(() => write.DeleteNexus(staleNexus)))
                .Should().Throw<KeyNotFoundException>();

            write.Schema.TryGetPropertyKeyId("unused-property", out _).Should().BeFalse();
            write.Schema.TryGetPropertyKeyId("unused-set", out _).Should().BeFalse();
            write.Schema.TryGetEdgeTypeId("UNUSED_EDGE_TYPE", out _).Should().BeFalse();
            write.Schema.TryGetNexusTypeId("UNUSED_NEXUS_TYPE", out _).Should().BeFalse();
            write.Schema.ListRoles().Should().NotContain("UnusedFirstRole");
            write.Schema.ListRoles().Should().NotContain("UnusedStaleRole");
            write.VertexExists(source).Should().BeTrue();
            write.TryGetEdge(edge, out _).Should().BeTrue();
            write.GetNexusType(nexus).Should().Be("Fact");
            write.Commit();
        }
        sink.Batches.Should().BeEmpty();

        using (var delete = database.BeginWriteTransaction())
        {
            delete.DeleteEdge(edge);
            delete.DeleteNexus(nexus);
            delete.DeleteVertex(source);
            delete.Commit();
        }

        sink.Clear();
        using (var deleted = database.BeginWriteTransaction())
        {
            Action setDeletedVertex = () =>
            {
                PropertyValue value = PropertyValue.FromString("value");
                deleted.SetProperty(source, "deleted-property", in value);
            };
            Action setDeletedEdge = () =>
            {
                PropertyValue value = PropertyValue.FromString("value");
                deleted.SetProperty(edge, "deleted-property", in value);
            };
            Action setDeletedNexus = () =>
            {
                PropertyValue value = PropertyValue.FromString("value");
                deleted.SetProperty(nexus, "deleted-property", in value);
            };

            setDeletedVertex.Should().Throw<KeyNotFoundException>();
            setDeletedEdge.Should().Throw<KeyNotFoundException>();
            setDeletedNexus.Should().Throw<KeyNotFoundException>();
            deleted.Schema.TryGetPropertyKeyId("deleted-property", out _).Should().BeFalse();
            deleted.Commit();
        }
        sink.Batches.Should().BeEmpty();
    }
}

using FluentAssertions;
using Yatagarasu.Core;

namespace Yatagarasu.Client.Tests;

public sealed class PublicIdentityContractTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("invalid")]
    [InlineData("reserved")]
    [InlineData("stale")]
    [InlineData("deleted")]
    public void Missing_identity_matrix_rejects_every_mutation_without_changing_the_transaction(string state)
    {
        using var store = GraphStore.OpenMemory();
        using GraphWriteSession write = store.Advanced.BeginWrite();
        VertexKey vertex = write.CreateVertex("Entity");
        VertexKey peer = write.CreateVertex("Peer");
        EdgeKey edge = write.Connect(vertex, "LINK", peer);
        NexusKey nexus = write.CreateNexus("Fact", [new("Subject", vertex), new("Object", peer)]);
        write.Set(vertex, "marker", "live");
        write.Set(edge, "marker", "live");
        write.Set(nexus, "marker", "live");
        if (state == "deleted")
        {
            write.Delete(edge);
            write.Delete(nexus);
            write.Delete(vertex);
        }
        long InvalidValue(long live) => state switch
        {
            "default" => 0,
            "invalid" => -1,
            "reserved" => live | (1L << 60),
            "stale" => live + (1L << 44),
            _ => live,
        };
        VertexKey missingVertex = new(new VertexId(InvalidValue(vertex.ToCore().Value)));
        EdgeKey missingEdge = new(new EdgeId(InvalidValue(edge.ToCore().Value)));
        NexusKey missingNexus = new(new NexusId(InvalidValue(nexus.ToCore().Value)));
        Action[] mutations =
        [
            () => write.Set(missingVertex, "unused", "value"),
            () => write.Set(missingEdge, "unused", "value"),
            () => write.Set(missingNexus, "unused", "value"),
            () => write.AddValue(missingVertex, "unused-set", "value"),
            () => write.AddValue(missingEdge, "unused-set", "value"),
            () => write.AddValue(missingNexus, "unused-set", "value"),
            () => write.Remove(missingVertex, "marker"),
            () => write.Remove(missingEdge, "marker"),
            () => write.Remove(missingNexus, "marker"),
            () => write.RemoveValue(missingVertex, "unused-set", "value"),
            () => write.RemoveValue(missingEdge, "unused-set", "value"),
            () => write.RemoveValue(missingNexus, "unused-set", "value"),
            () => write.Delete(missingVertex),
            () => write.Delete(missingEdge),
            () => write.Delete(missingNexus),
            () => write.Connect(missingVertex, "UNUSED", peer),
            () => write.Connect(peer, "UNUSED", missingVertex),
            () => write.CreateNexus("Unused", [new("A", peer), new("B", missingVertex), new("C", peer)]),
            () => write.SetVector(missingVertex, "unused-vector", [1f]),
        ];
        foreach (Action mutation in mutations) mutation.Should().Throw<KeyNotFoundException>();
        write.Contains(missingVertex).Should().BeFalse();
        write.TryGet(missingVertex, "marker", out _).Should().BeFalse();
        write.TryGet(missingEdge, "marker", out _).Should().BeFalse();
        write.TryGet(missingNexus, "marker", out _).Should().BeFalse();
        write.GetEdges(missingVertex).Should().BeEmpty();
        write.GetMembers(missingNexus).Should().BeEmpty();
        write.Query.Vertices(missingVertex).ToList().Should().BeEmpty();
        write.Transaction.Schema.ListPropertyKeys().Should().Equal("marker");
        write.Transaction.Schema.ListEdgeTypes().Should().Equal("LINK");
        write.Transaction.Schema.ListNexusTypes().Should().Equal("Fact");
        write.Transaction.Schema.ListRoles().Should().BeEquivalentTo("Subject", "Object");
        if (state != "deleted")
        {
            write.Get(vertex, "marker").AsString().Should().Be("live");
            write.Get(edge, "marker").AsString().Should().Be("live");
            write.Get(nexus, "marker").AsString().Should().Be("live");
        }
        write.Commit();
    }

    [Fact]
    public void Default_keys_are_invalid_and_read_as_missing()
    {
        using var store = GraphStore.OpenMemory();
        (VertexKey first, VertexKey second, EdgeKey edge, NexusKey nexus) = store.Write(write =>
        {
            VertexKey source = write.CreateVertex("Entity");
            VertexKey target = write.CreateVertex("Entity");
            EdgeKey createdEdge = write.Connect(source, "LINK", target);
            NexusKey createdNexus = write.CreateNexus("Fact",
            [
                new GraphNexusMember("Subject", source),
                new GraphNexusMember("Object", target),
            ]);
            write.Set(source, "marker", "vertex");
            write.Set(createdEdge, "marker", "edge");
            write.Set(createdNexus, "marker", "nexus");
            return (source, target, createdEdge, createdNexus);
        });

        default(VertexKey).IsValid.Should().BeFalse();
        default(EdgeKey).IsValid.Should().BeFalse();
        default(NexusKey).IsValid.Should().BeFalse();
        var invalidVertex = new VertexKey(VertexId.Invalid);
        var invalidEdge = new EdgeKey(EdgeId.Invalid);
        var invalidNexus = new NexusKey(NexusId.Invalid);
        invalidVertex.IsValid.Should().BeFalse();
        invalidEdge.IsValid.Should().BeFalse();
        invalidNexus.IsValid.Should().BeFalse();

        store.Read(read =>
        {
            read.Contains(default).Should().BeFalse();
            read.GetLabel(default).Should().BeNull();
            read.TryGet(default(VertexKey), "marker", out _).Should().BeFalse();
            read.TryGet(default(EdgeKey), "marker", out _).Should().BeFalse();
            read.TryGet(default(NexusKey), "marker", out _).Should().BeFalse();
            read.GetValues(default(VertexKey), "tags").Should().BeEmpty();
            read.GetValues(default(EdgeKey), "tags").Should().BeEmpty();
            read.GetValues(default(NexusKey), "tags").Should().BeEmpty();
            read.GetEdges(default).Should().BeEmpty();
            read.GetNeighbors(default).Should().BeEmpty();
            read.GetMembers(default).Should().BeEmpty();
            read.GetNexuses(default).Should().BeEmpty();
            read.Query.Vertices(default(VertexKey)).ToList().Should().BeEmpty();
            read.Contains(invalidVertex).Should().BeFalse();
            read.TryGet(invalidEdge, "marker", out _).Should().BeFalse();
            read.GetMembers(invalidNexus).Should().BeEmpty();

            read.Contains(first).Should().BeTrue();
            read.Contains(second).Should().BeTrue();
            read.GetEdges(first).Should().ContainSingle().Which.Key.Should().Be(edge);
            read.GetMembers(nexus).Should().HaveCount(2);
        });
    }

    [Fact]
    public void Default_keys_are_rejected_before_any_mutation()
    {
        using var store = GraphStore.OpenMemory();
        (VertexKey first, VertexKey second, EdgeKey edge, NexusKey nexus) = store.Write(write =>
        {
            VertexKey source = write.CreateVertex("Entity");
            VertexKey target = write.CreateVertex("Entity");
            EdgeKey createdEdge = write.Connect(source, "LINK", target);
            NexusKey createdNexus = write.CreateNexus("Fact",
            [
                new GraphNexusMember("Subject", source),
                new GraphNexusMember("Object", target),
            ]);
            return (source, target, createdEdge, createdNexus);
        });

        AssertMissingWrite(store, write => write.Delete(default(VertexKey)));
        AssertMissingWrite(store, write => write.Delete(default(EdgeKey)));
        AssertMissingWrite(store, write => write.Delete(default(NexusKey)));
        AssertMissingWrite(store, write => write.Set(default(VertexKey), "new-key", "value"));
        AssertMissingWrite(store, write => write.Set(default(EdgeKey), "new-key", "value"));
        AssertMissingWrite(store, write => write.Set(default(NexusKey), "new-key", "value"));
        AssertMissingWrite(store, write => write.Remove(default(VertexKey), "new-key"));
        AssertMissingWrite(store, write => write.Remove(default(EdgeKey), "new-key"));
        AssertMissingWrite(store, write => write.Remove(default(NexusKey), "new-key"));
        AssertMissingWrite(store, write => write.AddValue(default(VertexKey), "new-set", "value"));
        AssertMissingWrite(store, write => write.AddValue(default(EdgeKey), "new-set", "value"));
        AssertMissingWrite(store, write => write.AddValue(default(NexusKey), "new-set", "value"));
        AssertMissingWrite(store, write => write.RemoveValue(default(VertexKey), "new-set", "value"));
        AssertMissingWrite(store, write => write.RemoveValue(default(EdgeKey), "new-set", "value"));
        AssertMissingWrite(store, write => write.RemoveValue(default(NexusKey), "new-set", "value"));
        AssertMissingWrite(store, write => write.SetVector(default, "embedding", [1f, 2f]));
        AssertMissingWrite(store, write => write.Connect(default, "NEW-LINK", second));
        AssertMissingWrite(store, write => write.Connect(first, "NEW-LINK", default));
        AssertMissingWrite(store, write => write.CreateNexus("NewFact",
        [
            new GraphNexusMember("Subject", first),
            new GraphNexusMember("Object", default),
        ]));
        AssertMissingWrite(store, write => write.Delete(new VertexKey(VertexId.Invalid)));
        AssertMissingWrite(store, write => write.Delete(new EdgeKey(EdgeId.Invalid)));
        AssertMissingWrite(store, write => write.Delete(new NexusKey(NexusId.Invalid)));

        store.Read(read =>
        {
            read.Contains(first).Should().BeTrue();
            read.Contains(second).Should().BeTrue();
            read.GetEdges(first).Should().ContainSingle().Which.Key.Should().Be(edge);
            read.GetMembers(nexus).Should().HaveCount(2);
            read.TryGet(first, "new-key", out _).Should().BeFalse();
            read.TryGet(edge, "new-key", out _).Should().BeFalse();
            read.TryGet(nexus, "new-key", out _).Should().BeFalse();
        });
        DatabaseStatistics statistics = store.Advanced.GetStatistics();
        statistics.VertexCount.Should().Be(2);
        statistics.EdgeCount.Should().Be(1);
        statistics.NexusCount.Should().Be(1);
    }

    [Fact]
    public void Stale_vertex_key_cannot_observe_or_mutate_a_reused_vertex()
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_public_identity_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        VertexKey stale;
        VertexKey replacement;
        VertexKey peer;

        try
        {
            using (var store = GraphStore.Open(path))
            {
                (stale, peer) = store.Write(write =>
                {
                    VertexKey old = write.CreateVertex("Old");
                    VertexKey other = write.CreateVertex("Peer");
                    write.Set(old, "marker", "old");
                    return (old, other);
                });

                using (GraphReadSession snapshot = store.Advanced.BeginRead())
                {
                    store.Write(write => write.Delete(stale));
                    snapshot.Contains(stale).Should().BeTrue();
                    store.Read(read => read.Contains(stale)).Should().BeFalse();
                    store.Advanced.Vacuum().ReclaimedVertices.Should().Be(0);
                }

                store.Advanced.Vacuum().ReclaimedVertices.Should().Be(1);
                replacement = store.Write(write =>
                {
                    VertexKey created = write.CreateVertex("Replacement");
                    write.Set(created, "marker", "replacement");
                    write.Connect(created, "LINK", peer);
                    write.CreateNexus("Fact", [new("Subject", created), new("Object", peer)]);
                    return created;
                });
                replacement.ToCore().Sequence.Should().Be(stale.ToCore().Sequence);
                replacement.ToCore().Generation.Should().Be(stale.ToCore().Generation + 1);

                store.Read(read =>
                {
                    read.Contains(stale).Should().BeFalse();
                    read.GetLabel(stale).Should().BeNull();
                    read.TryGet(stale, "marker", out _).Should().BeFalse();
                    read.GetValues(stale, "tags").Should().BeEmpty();
                    read.GetEdges(stale).Should().BeEmpty();
                    read.GetNexuses(stale).Should().BeEmpty();
                    read.Query.Vertices(stale).ToList().Should().BeEmpty();
                });

                AssertMissingWrite(store, write => write.Delete(stale));
                AssertMissingWrite(store, write => write.Set(stale, "new-key", "value"));
                AssertMissingWrite(store, write => write.Remove(stale, "new-key"));
                AssertMissingWrite(store, write => write.AddValue(stale, "new-set", "value"));
                AssertMissingWrite(store, write => write.RemoveValue(stale, "new-set", "value"));
                AssertMissingWrite(store, write => write.SetVector(stale, "embedding", [1f]));
                AssertMissingWrite(store, write => write.Connect(stale, "NEW-LINK", peer));
                AssertMissingWrite(store, write => write.Connect(peer, "NEW-LINK", stale));
                AssertMissingWrite(store, write => write.CreateNexus("NewFact",
                [
                    new GraphNexusMember("Subject", peer),
                    new GraphNexusMember("Object", stale),
                ]));

                store.Read(read =>
                {
                    read.Contains(replacement).Should().BeTrue();
                    read.GetLabel(replacement).Should().Be("Replacement");
                    read.TryGet(replacement, "new-key", out _).Should().BeFalse();
                    read.Get(replacement, "marker").AsString().Should().Be("replacement");
                    read.GetEdges(peer).Should().ContainSingle();
                    read.GetNexuses(peer).Should().ContainSingle();
                });
            }

            using var reopened = GraphStore.Open(path);
            reopened.Read(read =>
            {
                read.Contains(stale).Should().BeFalse();
                read.Contains(replacement).Should().BeTrue();
                read.Contains(peer).Should().BeTrue();
                read.Query.Vertices(stale).ToList().Should().BeEmpty();
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Stale_edge_and_nexus_keys_cannot_mutate_reused_entities()
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_public_relation_identity_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        EdgeKey staleEdge;
        EdgeKey replacementEdge;
        NexusKey staleNexus;
        NexusKey replacementNexus;
        VertexKey source;
        VertexKey target;

        try
        {
            using (var store = GraphStore.Open(path))
            {
                (source, target, staleEdge, staleNexus) = store.Write(write =>
                {
                    VertexKey a = write.CreateVertex("Entity");
                    VertexKey b = write.CreateVertex("Entity");
                    EdgeKey edge = write.Connect(a, "LINK", b);
                    NexusKey nexus = write.CreateNexus("Fact",
                    [
                        new GraphNexusMember("Subject", a),
                        new GraphNexusMember("Object", b),
                    ]);
                    write.Set(edge, "marker", "old-edge");
                    write.Set(nexus, "marker", "old-nexus");
                    return (a, b, edge, nexus);
                });

                store.Write(write =>
                {
                    write.Delete(staleEdge);
                    write.Delete(staleNexus);
                });
                var report = store.Advanced.Vacuum();
                report.ReclaimedEdges.Should().Be(1);
                report.ReclaimedNexuses.Should().Be(1);

                (replacementEdge, replacementNexus) = store.Write(write =>
                {
                    EdgeKey edge = write.Connect(source, "LINK", target);
                    NexusKey nexus = write.CreateNexus("Fact",
                    [
                        new GraphNexusMember("Subject", source),
                        new GraphNexusMember("Object", target),
                    ]);
                    return (edge, nexus);
                });
                replacementEdge.ToCore().Sequence.Should().Be(staleEdge.ToCore().Sequence);
                replacementEdge.ToCore().Generation.Should().Be(staleEdge.ToCore().Generation + 1);
                replacementNexus.ToCore().Sequence.Should().Be(staleNexus.ToCore().Sequence);
                replacementNexus.ToCore().Generation.Should().Be(staleNexus.ToCore().Generation + 1);

                store.Read(read =>
                {
                    read.TryGet(staleEdge, "marker", out _).Should().BeFalse();
                    read.GetValues(staleEdge, "tags").Should().BeEmpty();
                    read.TryGet(staleNexus, "marker", out _).Should().BeFalse();
                    read.GetValues(staleNexus, "tags").Should().BeEmpty();
                    read.GetMembers(staleNexus).Should().BeEmpty();
                });

                AssertMissingWrite(store, write => write.Delete(staleEdge));
                AssertMissingWrite(store, write => write.Set(staleEdge, "new-key", "value"));
                AssertMissingWrite(store, write => write.Remove(staleEdge, "new-key"));
                AssertMissingWrite(store, write => write.AddValue(staleEdge, "new-set", "value"));
                AssertMissingWrite(store, write => write.RemoveValue(staleEdge, "new-set", "value"));
                AssertMissingWrite(store, write => write.Delete(staleNexus));
                AssertMissingWrite(store, write => write.Set(staleNexus, "new-key", "value"));
                AssertMissingWrite(store, write => write.Remove(staleNexus, "new-key"));
                AssertMissingWrite(store, write => write.AddValue(staleNexus, "new-set", "value"));
                AssertMissingWrite(store, write => write.RemoveValue(staleNexus, "new-set", "value"));

                store.Read(read =>
                {
                    read.GetEdges(source).Should().ContainSingle().Which.Key.Should().Be(replacementEdge);
                    read.GetMembers(replacementNexus).Should().HaveCount(2);
                    read.TryGet(replacementEdge, "new-key", out _).Should().BeFalse();
                    read.TryGet(replacementNexus, "new-key", out _).Should().BeFalse();
                });
            }

            using var reopened = GraphStore.Open(path);
            reopened.Read(read =>
            {
                read.TryGet(staleEdge, "marker", out _).Should().BeFalse();
                read.GetMembers(staleNexus).Should().BeEmpty();
                read.GetEdges(source).Should().ContainSingle().Which.Key.Should().Be(replacementEdge);
                read.GetMembers(replacementNexus).Should().HaveCount(2);
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertMissingWrite(GraphStore store, Action<GraphWriteSession> operation)
    {
        using GraphWriteSession write = store.Advanced.BeginWrite();
        Action mutate = () => operation(write);
        mutate.Should().Throw<KeyNotFoundException>();
    }
}

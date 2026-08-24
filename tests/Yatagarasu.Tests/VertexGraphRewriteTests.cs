using System.Text;
using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Logical;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class VertexGraphRewriteTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "yatagarasu-vertex-rewrite-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "graph.yata");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Batch_rewrite_applies_complete_mapping_once_to_self_loops_and_multi_role_nexuses()
    {
        var sink = new InMemoryLogicalMutationSink();
        using var database = YatagarasuDatabase.Open(
            DatabasePath,
            new YatagarasuDatabaseOptions { LogicalMutationSink = sink });
        database.EditSchema(schema =>
        {
            schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);
            schema.CreateIndex(new ScalarIndexDefinition(
                "idx_new_string",
                new PropertyTarget(PropertyOwnerKind.Vertex, "string", "NewA"),
                IndexKind.StringEquality));
            schema.CreateIndex(new FullTextIndexDefinition(
                "idx_new_text",
                new PropertyTarget(PropertyOwnerKind.Vertex, "string", "NewA")));
            schema.CreateIndex(new VectorIndexDefinition(
                "idx_new_vector",
                new PropertyTarget(PropertyOwnerKind.Vertex, "vector", "NewA"),
                2));
        });

        VertexId first;
        VertexId second;
        VertexId outside;
        EdgeId selfLoop;
        EdgeId between;
        EdgeId outgoing;
        NexusId nexus;
        using (IWriteTransaction seed = database.BeginWriteTransaction())
        {
            first = seed.CreateVertex("OldA");
            second = seed.CreateVertex("OldB");
            outside = seed.CreateVertex("Outside");
            seed.SetProperty(first, "bool", PropertyValue.FromBool(true));
            seed.SetProperty(first, "int32", PropertyValue.FromInt32(32));
            seed.SetProperty(first, "int64", PropertyValue.FromInt64(64));
            seed.SetProperty(first, "double", PropertyValue.FromDouble(1.25));
            seed.SetProperty(first, "string", PropertyValue.FromString("value"));
            seed.SetProperty(first, "bytes", PropertyValue.FromBytes([1, 2, 255]));
            seed.SetProperty(first, "vector", PropertyValue.FromFloatArray([1.5f, 2.5f]));
            seed.AddPropertyValue(first, "tags", PropertyValue.FromString("first"));
            seed.AddPropertyValue(first, "tags", PropertyValue.FromString("second"));

            selfLoop = seed.CreateEdge(first, first, "SELF");
            between = seed.CreateEdge(first, second, "BETWEEN");
            outgoing = seed.CreateEdge(second, outside, "OUT");
            seed.SetProperty(between, "weight", PropertyValue.FromInt32(7));
            nexus = seed.CreateNexus("Fact", [
                new("Subject", first),
                new("Object", first),
                new("Peer", second),
                new("Outside", outside),
            ]);
            seed.SetProperty(nexus, "confidence", PropertyValue.FromDouble(0.75));
            seed.Commit();
        }
        sink.Clear();

        using IReadTransaction oldSnapshot = database.BeginReadTransaction();
        VertexGraphRewriteResult result;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            result = write.ReplaceVertices([
                new VertexRewriteRequest(first, "NewA"),
                new VertexRewriteRequest(second, "NewB"),
            ]);
            write.Commit();
        }

        result.VertexMappings.Should().HaveCount(2);
        result.EdgeMappings.Select(x => x.OldId)
            .Should().BeEquivalentTo([selfLoop, between, outgoing]);
        result.NexusMappings.Select(x => x.OldId).Should().Equal(nexus);
        VertexId newFirst = result.VertexMappings.Single(x => x.OldId == first).NewId;
        VertexId newSecond = result.VertexMappings.Single(x => x.OldId == second).NewId;

        oldSnapshot.VertexExists(first).Should().BeTrue();
        oldSnapshot.VertexExists(result.VertexMappings[0].NewId).Should().BeFalse();
        oldSnapshot.TryGetEdge(between, out _).Should().BeTrue();
        oldSnapshot.TryGetEdge(result.EdgeMappings.Single(x => x.OldId == between).NewId, out _)
            .Should().BeFalse();
        oldSnapshot.GetNexusType(nexus).Should().Be("Fact");
        oldSnapshot.GetNexusType(result.NexusMappings.Single().NewId).Should().BeNull();

        using IReadTransaction read = database.BeginReadTransaction();
        read.VertexExists(first).Should().BeFalse();
        read.VertexExists(second).Should().BeFalse();
        read.GetVertexLabel(newFirst).Should().Be("NewA");
        read.GetVertexLabel(newSecond).Should().Be("NewB");
        AssertAllProperties(read, newFirst);

        EdgeId newSelf = result.EdgeMappings.Single(x => x.OldId == selfLoop).NewId;
        EdgeId newBetween = result.EdgeMappings.Single(x => x.OldId == between).NewId;
        EdgeId newOutgoing = result.EdgeMappings.Single(x => x.OldId == outgoing).NewId;
        read.TryGetEdge(newSelf, out EdgeInfo self).Should().BeTrue();
        self.Source.Should().Be(newFirst);
        self.Target.Should().Be(newFirst);
        read.TryGetEdge(newBetween, out EdgeInfo connected).Should().BeTrue();
        connected.Source.Should().Be(newFirst);
        connected.Target.Should().Be(newSecond);
        read.GetProperty(newBetween, "weight").Int32Value.Should().Be(7);
        read.TryGetEdge(newOutgoing, out EdgeInfo external).Should().BeTrue();
        external.Source.Should().Be(newSecond);
        external.Target.Should().Be(outside);

        NexusId newNexus = result.NexusMappings.Single().NewId;
        ReadMembers(read.GetMembers(newNexus)).Should().BeEquivalentTo([
            new NexusMember("Subject", newFirst),
            new NexusMember("Object", newFirst),
            new NexusMember("Peer", newSecond),
            new NexusMember("Outside", outside),
        ]);
        read.GetProperty(newNexus, "confidence").DoubleValue.Should().Be(0.75);

        ReadEntities(read.SeekIndex("idx_new_string", PropertyValue.FromString("value")))
            .Should().ContainSingle().Which.Should().Be(EntityRef.From(newFirst));
        read.Query.Search("idx_new_text", "value", 10).ToList()
            .Should().ContainSingle().Which.Should().Be(newFirst);
        using (VectorSearchCursor vectorHits = read.KnnSearch(
                   "idx_new_vector",
                   [1.5f, 2.5f],
                   10))
        {
            vectorHits.MoveNext().Should().BeTrue();
            vectorHits.Current.Owner.Should().Be(EntityRef.From(newFirst));
        }

        sink.Mutations.Count(m => m.Kind == LogicalMutationKind.CreateEdge).Should().Be(3);
        sink.Mutations.Count(m => m.Kind == LogicalMutationKind.DeleteEdge).Should().Be(3);
        sink.Mutations.Count(m => m.Kind == LogicalMutationKind.CreateNexus).Should().Be(1);
        sink.Mutations.Count(m => m.Kind == LogicalMutationKind.DeleteNexus).Should().Be(1);
    }

    [Fact]
    public void Rewrite_rejects_missing_stale_duplicate_and_empty_requests_before_mutation()
    {
        using var database = YatagarasuDatabase.Open(DatabasePath);
        VertexId vertex;
        using (IWriteTransaction seed = database.BeginWriteTransaction())
        {
            vertex = seed.CreateVertex("Old");
            seed.Commit();
        }

        using IWriteTransaction write = database.BeginWriteTransaction();
        Action empty = () => write.ReplaceVertices([]);
        Action duplicate = () => write.ReplaceVertices([
            new VertexRewriteRequest(vertex, "A"),
            new VertexRewriteRequest(vertex, "B"),
        ]);
        Action missing = () => write.ReplaceVertex(VertexId.Create(vertex.Sequence + 100, 1), "New");
        Action stale = () => write.ReplaceVertex(
            VertexId.Create(vertex.Sequence, vertex.Generation + 1),
            "New");

        empty.Should().Throw<ArgumentException>();
        duplicate.Should().Throw<ArgumentException>();
        missing.Should().Throw<KeyNotFoundException>();
        stale.Should().Throw<KeyNotFoundException>();
        write.VertexExists(vertex).Should().BeTrue();
        write.Rollback();
    }

    [Fact]
    public void Caller_rollback_restores_graph_after_a_later_migration_failure()
    {
        using var database = YatagarasuDatabase.Open(DatabasePath);
        VertexId oldVertex;
        VertexId other;
        EdgeId oldEdge;
        using (IWriteTransaction seed = database.BeginWriteTransaction())
        {
            oldVertex = seed.CreateVertex("Old");
            other = seed.CreateVertex("Other");
            oldEdge = seed.CreateEdge(oldVertex, other, "LINK");
            seed.Commit();
        }

        VertexGraphRewriteResult provisional;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            provisional = write.ReplaceVertex(oldVertex, "New");
            write.Rollback();
        }

        using IReadTransaction read = database.BeginReadTransaction();
        read.VertexExists(oldVertex).Should().BeTrue();
        read.TryGetEdge(oldEdge, out EdgeInfo oldInfo).Should().BeTrue();
        oldInfo.Source.Should().Be(oldVertex);
        read.VertexExists(provisional.VertexMappings.Single().NewId).Should().BeFalse();
        read.TryGetEdge(provisional.EdgeMappings.Single().NewId, out _).Should().BeFalse();
    }

    private static void AssertAllProperties(IReadTransaction read, VertexId vertex)
    {
        read.GetProperty(vertex, "bool").BoolValue.Should().BeTrue();
        read.GetProperty(vertex, "int32").Int32Value.Should().Be(32);
        read.GetProperty(vertex, "int64").Int64Value.Should().Be(64);
        read.GetProperty(vertex, "double").DoubleValue.Should().Be(1.25);
        Encoding.UTF8.GetString(read.GetProperty(vertex, "string").Utf8StringValue)
            .Should().Be("value");
        read.GetProperty(vertex, "bytes").BytesValue.ToArray().Should().Equal(1, 2, 255);
        read.GetProperty(vertex, "vector").FloatArrayValue.ToArray().Should().Equal(1.5f, 2.5f);
        ReadStrings(read.GetPropertyValues(vertex, "tags"))
            .Should().BeEquivalentTo("first", "second");
    }

    private static List<string> ReadStrings(PropertyValuesEnumerator values)
    {
        var result = new List<string>();
        while (values.MoveNext())
            result.Add(Encoding.UTF8.GetString(values.Current.Utf8StringValue));
        values.Dispose();
        return result;
    }

    private static List<NexusMember> ReadMembers(NexusMemberEnumerator members)
    {
        var result = new List<NexusMember>();
        while (members.MoveNext())
            result.Add(members.Current);
        members.Dispose();
        return result;
    }

    private static List<EntityRef> ReadEntities(EntityRefEnumerator entities)
    {
        var result = new List<EntityRef>();
        while (entities.MoveNext())
            result.Add(entities.Current);
        entities.Dispose();
        return result;
    }
}

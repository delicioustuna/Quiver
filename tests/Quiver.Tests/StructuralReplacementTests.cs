using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class StructuralReplacementTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "quiver-replacement-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "graph.quiver");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void TryGetEdge_and_property_enumeration_use_logical_identity()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        EdgeId edge;
        VertexId source;
        VertexId target;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            source = write.CreateVertex("Source");
            target = write.CreateVertex("Target");
            edge = write.CreateEdge(source, target, "LINK");
            write.SetProperty(edge, "weight", PropertyValue.FromInt32(7));
            write.Commit();
        }

        using IReadTransaction read = database.BeginReadTransaction();
        read.TryGetEdge(edge, out EdgeInfo info).Should().BeTrue();
        info.Should().Be(new EdgeInfo(edge, source, target, "LINK"));
        read.TryGetEdge(new EdgeId(edge.Sequence), out _).Should().BeFalse();

        var properties = read.EnumerateProperties(edge);
        properties.MoveNext().Should().BeTrue();
        properties.Current.Value.Int32Value.Should().Be(7);
        properties.MoveNext().Should().BeFalse();

        var missing = read.EnumerateProperties(new EdgeId(edge.Sequence));
        missing.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void ReplaceEdge_preserves_every_property_type_and_cardinality_across_snapshots()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        database.EditSchema(schema =>
            schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));

        EdgeId oldId;
        VertexId oldSource;
        VertexId oldTarget;
        VertexId newSource;
        VertexId newTarget;
        using (IWriteTransaction seed = database.BeginWriteTransaction())
        {
            oldSource = seed.CreateVertex("Vertex");
            oldTarget = seed.CreateVertex("Vertex");
            newSource = seed.CreateVertex("Vertex");
            newTarget = seed.CreateVertex("Vertex");
            oldId = seed.CreateEdge(oldSource, oldTarget, "OLD");
            seed.SetProperty(oldId, "bool", PropertyValue.FromBool(true));
            seed.SetProperty(oldId, "int32", PropertyValue.FromInt32(32));
            seed.SetProperty(oldId, "int64", PropertyValue.FromInt64(64));
            seed.SetProperty(oldId, "double", PropertyValue.FromDouble(1.25));
            seed.SetProperty(oldId, "string", PropertyValue.FromString("value"));
            seed.SetProperty(oldId, "bytes", PropertyValue.FromBytes([1, 2, 255]));
            seed.SetProperty(oldId, "vector", PropertyValue.FromFloatArray([1.5f, 2.5f]));
            seed.AddPropertyValue(oldId, "tags", PropertyValue.FromString("first"));
            seed.AddPropertyValue(oldId, "tags", PropertyValue.FromString("second"));
            seed.Commit();
        }

        using IReadTransaction oldSnapshot = database.BeginReadTransaction();
        EdgeReplacement replacement;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            replacement = write.ReplaceEdge(oldId, newSource, newTarget, "NEW");
            replacement.OldId.Should().Be(oldId);
            replacement.NewId.Should().NotBe(oldId);
            write.Commit();
        }

        oldSnapshot.TryGetEdge(oldId, out EdgeInfo oldInfo).Should().BeTrue();
        oldInfo.Should().Be(new EdgeInfo(oldId, oldSource, oldTarget, "OLD"));
        oldSnapshot.TryGetEdge(replacement.NewId, out _).Should().BeFalse();

        using IReadTransaction current = database.BeginReadTransaction();
        current.TryGetEdge(oldId, out _).Should().BeFalse();
        current.TryGetEdge(replacement.NewId, out EdgeInfo newInfo).Should().BeTrue();
        newInfo.Should().Be(new EdgeInfo(replacement.NewId, newSource, newTarget, "NEW"));
        current.GetProperty(replacement.NewId, "bool").BoolValue.Should().BeTrue();
        current.GetProperty(replacement.NewId, "int32").Int32Value.Should().Be(32);
        current.GetProperty(replacement.NewId, "int64").Int64Value.Should().Be(64);
        current.GetProperty(replacement.NewId, "double").DoubleValue.Should().Be(1.25);
        System.Text.Encoding.UTF8.GetString(
            current.GetProperty(replacement.NewId, "string").Utf8StringValue).Should().Be("value");
        current.GetProperty(replacement.NewId, "bytes").BytesValue.ToArray().Should().Equal(1, 2, 255);
        current.GetProperty(replacement.NewId, "vector").FloatArrayValue.ToArray().Should().Equal(1.5f, 2.5f);
        ReadStrings(current.GetPropertyValues(replacement.NewId, "tags"))
            .Should().BeEquivalentTo("first", "second");
    }

    [Fact]
    public void ReplaceNexus_preserves_properties_and_replaces_all_members()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        database.EditSchema(schema =>
            schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));

        NexusId oldId;
        VertexId first;
        VertexId second;
        VertexId third;
        using (IWriteTransaction seed = database.BeginWriteTransaction())
        {
            first = seed.CreateVertex("Vertex");
            second = seed.CreateVertex("Vertex");
            third = seed.CreateVertex("Vertex");
            oldId = seed.CreateNexus("OLD", [new("Left", first), new("Right", second)]);
            seed.SetProperty(oldId, "payload", PropertyValue.FromBytes([4, 5, 6]));
            seed.AddPropertyValue(oldId, "tags", PropertyValue.FromInt64(10));
            seed.AddPropertyValue(oldId, "tags", PropertyValue.FromInt64(20));
            seed.Commit();
        }

        using IReadTransaction oldSnapshot = database.BeginReadTransaction();
        NexusReplacement replacement;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            replacement = write.ReplaceNexus(
                oldId,
                "NEW",
                [new("Subject", first), new("Object", third)]);
            write.Commit();
        }

        oldSnapshot.GetNexusType(oldId).Should().Be("OLD");
        oldSnapshot.GetNexusType(replacement.NewId).Should().BeNull();

        using IReadTransaction current = database.BeginReadTransaction();
        current.GetNexusType(oldId).Should().BeNull();
        current.GetNexusType(replacement.NewId).Should().Be("NEW");
        current.GetProperty(replacement.NewId, "payload").BytesValue.ToArray().Should().Equal(4, 5, 6);
        ReadInt64(current.GetPropertyValues(replacement.NewId, "tags"))
            .Should().BeEquivalentTo([10L, 20L]);
        ReadMembers(current.GetMembers(replacement.NewId)).Should().BeEquivalentTo([
            new NexusMember("Subject", first),
            new NexusMember("Object", third),
        ]);
    }

    [Fact]
    public void ReplaceNexus_validation_failure_keeps_old_nexus_visible()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        using IWriteTransaction write = database.BeginWriteTransaction();
        VertexId first = write.CreateVertex("Vertex");
        VertexId second = write.CreateVertex("Vertex");
        NexusId oldId = write.CreateNexus("Fact", [new("Left", first), new("Right", second)]);

        Action replace = () => write.ReplaceNexus(
            oldId,
            "Fact",
            [new("Same", first), new("Same", first)]);

        replace.Should().Throw<ArgumentException>();
        write.GetNexusType(oldId).Should().Be("Fact");
    }

    private static List<string> ReadStrings(PropertyValuesEnumerator values)
    {
        var result = new List<string>();
        while (values.MoveNext())
            result.Add(System.Text.Encoding.UTF8.GetString(values.Current.Utf8StringValue));
        values.Dispose();
        return result;
    }

    private static List<long> ReadInt64(PropertyValuesEnumerator values)
    {
        var result = new List<long>();
        while (values.MoveNext())
            result.Add(values.Current.Int64Value);
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
}

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class GraphJsonExportTests
{
    [Fact]
    public void Missing_identity_metadata_is_not_created_by_read_probe()
    {
        using var container = new SingleFileContainer(new InMemoryPagedFile());
        var store = new DatabaseIdentityStore(container, createIfMissing: false);

        store.TryGet(out _).Should().BeFalse();
        container.HasTenant(DatabaseIdentityStore.TenantId).Should().BeFalse();

        DatabaseInstanceId created = store.EnsureCreated();
        created.Value.Should().NotBeEmpty();
        container.HasTenant(DatabaseIdentityStore.TenantId).Should().BeTrue();
    }

    [Fact]
    public void Export_writes_type_faithful_graph_json()
    {
        using var db = QuiverDatabase.CreateInMemory();
        VertexId a;
        VertexId b;
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        {
            a = tx.CreateVertex("Document");
            b = tx.CreateVertex("Chunk");
            EdgeId edge = tx.CreateEdge(a, b, "CONTAINS");
            NexusId nexus = tx.CreateNexus(
                "Fact",
                [new NexusMember("subject", a), new NexusMember("source", b)]);

            tx.SetProperty(a, "active", PropertyValue.FromBool(true));
            tx.SetProperty(a, "rank", PropertyValue.FromInt32(7));
            tx.SetProperty(a, "ticks", PropertyValue.FromInt64(long.MaxValue));
            tx.SetProperty(a, "score", PropertyValue.FromDouble(double.NaN));
            tx.SetProperty(a, "title", PropertyValue.FromString("日本語"));
            tx.SetProperty(a, "blob", PropertyValue.FromBytes([1, 2, 255]));
            tx.SetProperty(
                a,
                "embedding",
                PropertyValue.FromFloatArray([1.5f, float.PositiveInfinity, float.NegativeInfinity]));
            tx.AddPropertyValue(a, "tags", PropertyValue.FromString("rag"));
            tx.AddPropertyValue(a, "tags", PropertyValue.FromString("local"));
            tx.SetProperty(edge, "weight", PropertyValue.FromDouble(0.25));
            tx.SetProperty(nexus, "confidence", PropertyValue.FromInt32(9));
            tx.Commit();
        }

        using var output = new MemoryStream();
        GraphJsonExportResult result = GraphJsonExporter.Export(db, output);
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        JsonElement root = document.RootElement;

        root.GetProperty("format").GetString().Should().Be("quiver-graph");
        root.GetProperty("version").GetInt32().Should().Be(1);
        root.GetProperty("source").GetProperty("databaseId").GetGuid()
            .Should().Be(result.SourceDatabaseId.Value);
        root.GetProperty("source").GetProperty("snapshot").GetGuid()
            .Should().Be(result.SnapshotId);
        result.VertexCount.Should().Be(2);
        result.EdgeCount.Should().Be(1);
        result.NexusCount.Should().Be(1);
        result.UsesTemporaryDatabaseId.Should().BeFalse();

        JsonElement vertex = root.GetProperty("vertices").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == a.Value.ToString());
        Dictionary<string, JsonElement[]> properties = vertex.GetProperty("properties")
            .EnumerateArray()
            .GroupBy(item => item.GetProperty("key").GetString()!)
            .ToDictionary(group => group.Key, group => group.ToArray());

        properties["ticks"].Single().GetProperty("value").GetString()
            .Should().Be(long.MaxValue.ToString());
        properties["score"].Single().GetProperty("value").GetString()
            .Should().Be("NaN");
        properties["blob"].Single().GetProperty("value").GetBytesFromBase64()
            .Should().Equal(1, 2, 255);
        properties["embedding"].Single().GetProperty("value").EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.GetSingle().ToString())
            .Should().Equal("1.5", "Infinity", "-Infinity");
        properties["tags"].Should().HaveCount(2)
            .And.OnlyContain(item => item.GetProperty("cardinality").GetString() == "set");

        root.GetProperty("edges")[0].GetProperty("source").GetString()
            .Should().Be(a.Value.ToString());
        root.GetProperty("nexuses")[0].GetProperty("members").GetArrayLength()
            .Should().Be(2);
    }

    [Fact]
    public void Vertex_selection_outputs_induced_edges_and_complete_nexuses()
    {
        using var db = QuiverDatabase.CreateInMemory();
        VertexId a;
        VertexId b;
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        {
            a = tx.CreateVertex("Person");
            b = tx.CreateVertex("Person");
            VertexId c = tx.CreateVertex("Person");
            tx.CreateEdge(a, b, "KNOWS");
            tx.CreateEdge(a, b, "WORKS_WITH");
            tx.CreateEdge(a, c, "KNOWS");
            tx.CreateNexus("Pair", [new("left", a), new("right", b)]);
            tx.CreateNexus("Pair", [new("left", a), new("right", c)]);
            tx.Commit();
        }

        using var output = new MemoryStream();
        GraphJsonExportResult result = GraphJsonExporter.Export(
            db,
            output,
            new GraphSelection(vertices: [a, b]));
        using JsonDocument document = JsonDocument.Parse(output.ToArray());

        result.VertexCount.Should().Be(2);
        result.EdgeCount.Should().Be(2);
        result.NexusCount.Should().Be(1);
        document.RootElement.GetProperty("vertices").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .Should().BeEquivalentTo(a.Value.ToString(), b.Value.ToString());
    }

    [Fact]
    public void Explicit_relations_add_complete_reference_closure_before_inducing_relations()
    {
        using var db = QuiverDatabase.CreateInMemory();
        EdgeId selectedEdge;
        NexusId selectedNexus;
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        {
            VertexId a = tx.CreateVertex("V");
            VertexId b = tx.CreateVertex("V");
            VertexId c = tx.CreateVertex("V");
            selectedEdge = tx.CreateEdge(a, b, "SELECTED");
            tx.CreateEdge(a, b, "INDUCED");
            selectedNexus = tx.CreateNexus("SelectedNexus", [new("a", a), new("c", c)]);
            tx.CreateNexus("InducedNexus", [new("x", a), new("y", b)]);
            tx.Commit();
        }

        using var output = new MemoryStream();
        GraphJsonExportResult result = GraphJsonExporter.Export(
            db,
            output,
            new GraphSelection(edges: [selectedEdge], nexuses: [selectedNexus]));

        result.VertexCount.Should().Be(3);
        result.EdgeCount.Should().Be(2);
        result.NexusCount.Should().Be(2);
    }

    [Fact]
    public void Vertex_selection_does_not_match_a_different_generation_of_the_same_sequence()
    {
        using var db = QuiverDatabase.CreateInMemory();
        VertexId current;
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        {
            current = tx.CreateVertex("V");
            tx.Commit();
        }
        VertexId stale = VertexId.Create(current.Sequence, current.Generation + 1);

        using var output = new MemoryStream();
        GraphJsonExportResult result = GraphJsonExporter.Export(
            db,
            output,
            new GraphSelection(vertices: [stale]));

        result.VertexCount.Should().Be(0);
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        document.RootElement.GetProperty("vertices").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Export_options_filter_heap_values_and_pretty_print()
    {
        using var db = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        {
            VertexId vertex = tx.CreateVertex("V");
            tx.SetProperty(vertex, "keep", PropertyValue.FromInt32(1));
            tx.SetProperty(vertex, "drop", PropertyValue.FromInt32(2));
            tx.SetProperty(vertex, "blob", PropertyValue.FromBytes([1]));
            tx.SetProperty(vertex, "vector", PropertyValue.FromFloatArray([1f]));
            tx.Commit();
        }

        using var output = new MemoryStream();
        GraphJsonExporter.Export(
            db,
            output,
            options: new GraphJsonExportOptions
            {
                WriteIndented = true,
                IncludeBytes = false,
                IncludeFloatArrays = false,
                IncludedPropertyKeys = ["keep", "blob", "vector"],
            });
        string json = Encoding.UTF8.GetString(output.ToArray());
        json.Should().Contain(Environment.NewLine);
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("vertices")[0].GetProperty("properties")
            .EnumerateArray()
            .Select(item => item.GetProperty("key").GetString())
            .Should().Equal("keep");
    }

    [Fact]
    public void Database_instance_id_survives_reopen_and_snapshot_copy()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "quiver_export_identity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "source.quiver");
        string snapshotPath = Path.Combine(directory, "snapshot.quiver");
        try
        {
            DatabaseInstanceId original;
            using (var db = QuiverDatabase.Open(sourcePath))
            {
                db.TryGetDatabaseInstanceId(out original).Should().BeTrue();
                db.CreateSnapshot(snapshotPath);
            }

            using (var reopened = QuiverDatabase.Open(sourcePath))
            {
                reopened.TryGetDatabaseInstanceId(out DatabaseInstanceId actual).Should().BeTrue();
                actual.Should().Be(original);
            }

            using (var snapshot = QuiverDatabase.Open(snapshotPath))
            {
                snapshot.TryGetDatabaseInstanceId(out DatabaseInstanceId actual).Should().BeTrue();
                actual.Should().Be(original);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Legacy_database_read_export_does_not_create_identity_metadata()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "quiver_export_legacy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "legacy.quiver");
        try
        {
            using (var legacyContainer = new SingleFileContainer(sourcePath))
            {
                legacyContainer.HasTenant(DatabaseIdentityStore.TenantId).Should().BeFalse();
            }

            using (var db = QuiverDatabase.Open(sourcePath))
            using (var output = new MemoryStream())
            {
                db.TryGetDatabaseInstanceId(out _).Should().BeFalse();
                GraphJsonExportResult result = GraphJsonExporter.Export(db, output);
                result.UsesTemporaryDatabaseId.Should().BeTrue();
                db.TryGetDatabaseInstanceId(out _).Should().BeFalse();
            }

            using var reopenedContainer = new SingleFileContainer(sourcePath);
            reopenedContainer.HasTenant(DatabaseIdentityStore.TenantId).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Legacy_identity_creation_does_not_join_an_active_writer_write_set()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "quiver_export_identity_lease_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "legacy.quiver");
        try
        {
            using (var legacyContainer = new SingleFileContainer(sourcePath))
            {
            }

            DatabaseInstanceId created;
            using (var db = QuiverDatabase.Open(sourcePath, new QuiverDatabaseOptions
            {
                WriterContentionMode = WriterContentionMode.FailFast,
            }))
            {
                db.TryGetDatabaseInstanceId(out _).Should().BeFalse();

                using (var active = db.BackendInternal.Transactions.BeginWrite())
                {
                    Action second = () => db.BeginWriteTransaction().Dispose();
                    second.Should().Throw<WriterBusyException>();
                    db.TryGetDatabaseInstanceId(out _).Should().BeFalse();
                    active.Abort();
                }

                using (IWriteTransaction write = db.BeginWriteTransaction())
                    write.Rollback();
                db.TryGetDatabaseInstanceId(out created).Should().BeTrue();
                created.Value.Should().NotBeEmpty();
            }

            using var reopened = QuiverDatabase.Open(sourcePath);
            reopened.TryGetDatabaseInstanceId(out DatabaseInstanceId durable).Should().BeTrue();
            durable.Should().Be(created);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Large_export_flushes_to_stream_incrementally()
    {
        using var db = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        {
            for (int i = 0; i < 2_000; i++)
            {
                VertexId vertex = tx.CreateVertex("V");
                tx.SetProperty(vertex, "text", PropertyValue.FromString(new string('x', 64)));
            }
            tx.Commit();
        }

        using var output = new CountingWriteStream();
        GraphJsonExportResult result = GraphJsonExporter.Export(db, output);

        result.VertexCount.Should().Be(2_000);
        output.WriteCount.Should().BeGreaterThan(1);
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        document.RootElement.GetProperty("vertices").GetArrayLength().Should().Be(2_000);
    }

    [Fact]
    public void Destination_failure_is_not_hidden()
    {
        using var db = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        {
            for (int i = 0; i < 500; i++)
                tx.CreateVertex("V");
            tx.Commit();
        }

        using var destination = new FailingWriteStream(128);
        Action act = () => GraphJsonExporter.Export(db, destination);
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Pre_canceled_export_writes_nothing_and_propagates_cancellation()
    {
        using var db = QuiverDatabase.CreateInMemory();
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Action act = () => GraphJsonExporter.Export(
            db,
            destination,
            cancellationToken: cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        destination.Length.Should().Be(0);
    }

    private sealed class CountingWriteStream : MemoryStream
    {
        internal int WriteCount { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCount++;
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteCount++;
            base.Write(buffer);
        }
    }

    private sealed class FailingWriteStream(int limit) : Stream
    {
        private int _remaining = limit;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > _remaining)
                throw new IOException("Injected destination failure.");
            _remaining -= buffer.Length;
        }
    }
}

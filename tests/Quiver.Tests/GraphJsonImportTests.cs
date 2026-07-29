using System.Text;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

public sealed class GraphJsonImportTests
{
    [Fact]
    public void Exported_graph_round_trips_with_types_cardinality_and_references()
    {
        using var source = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = source.BeginWriteTransaction())
        {
            VertexId a = tx.CreateVertex("Document");
            VertexId b = tx.CreateVertex("Chunk");
            EdgeId sourceEdge = tx.CreateEdge(a, b, "CONTAINS");
            NexusId nexus = tx.CreateNexus(
                "Evidence",
                [new NexusMember("document", a), new NexusMember("chunk", b)]);
            tx.SetProperty(a, "active", PropertyValue.FromBool(true));
            tx.SetProperty(a, "rank", PropertyValue.FromInt32(7));
            tx.SetProperty(a, "ticks", PropertyValue.FromInt64(long.MaxValue));
            tx.SetProperty(a, "score", PropertyValue.FromDouble(double.NaN));
            tx.SetProperty(a, "title", PropertyValue.FromString("日本語"));
            tx.SetProperty(a, "blob", PropertyValue.FromBytes([1, 2, 255]));
            tx.SetVectorProperty(EntityRef.From(a), "embedding", [1.5f, float.PositiveInfinity]);
            tx.AddPropertyValue(a, "tags", PropertyValue.FromString("rag"));
            tx.AddPropertyValue(a, "tags", PropertyValue.FromString("local"));
            tx.SetProperty(sourceEdge, "weight", PropertyValue.FromDouble(0.25));
            tx.SetProperty(nexus, "confidence", PropertyValue.FromInt32(9));
            tx.Commit();
        }

        using var json = new MemoryStream();
        GraphJsonExporter.Export(source, json);
        json.Position = 0;

        using var target = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = target.BeginWriteTransaction())
        {
            GraphJsonImportResult result = GraphJsonImporter.Import(tx, json);
            result.VertexCount.Should().Be(2);
            result.EdgeCount.Should().Be(1);
            result.NexusCount.Should().Be(1);
            tx.State.Should().Be(TransactionState.Active);
            tx.Commit();
        }

        using IReadTransaction read = target.BeginReadTransaction();
        VertexId document = read.Query.Vertices().HasLabel("Document").ToList().Should()
            .ContainSingle().Subject;
        VertexId chunk = read.Query.Vertices().HasLabel("Chunk").ToList().Should()
            .ContainSingle().Subject;
        read.GetProperty(document, "active").BoolValue.Should().BeTrue();
        read.GetProperty(document, "rank").Int32Value.Should().Be(7);
        read.GetProperty(document, "ticks").Int64Value.Should().Be(long.MaxValue);
        double.IsNaN(read.GetProperty(document, "score").DoubleValue).Should().BeTrue();
        Encoding.UTF8.GetString(read.GetProperty(document, "title").Utf8StringValue)
            .Should().Be("日本語");
        read.GetProperty(document, "blob").BytesValue.ToArray().Should().Equal(1, 2, 255);
        var vector = new float[2];
        read.TryGetVectorProperty(EntityRef.From(document), "embedding", vector).Should().BeTrue();
        vector[0].Should().Be(1.5f);
        float.IsPositiveInfinity(vector[1]).Should().BeTrue();

        using (PropertyValuesEnumerator tags = read.GetPropertyValues(document, "tags"))
        {
            var values = new List<string>();
            while (tags.MoveNext())
                values.Add(Encoding.UTF8.GetString(tags.Current.Utf8StringValue));
            values.Should().BeEquivalentTo("rag", "local");
        }

        EdgeId importedEdge = read.Query.Edges().ToList().Should().ContainSingle().Subject;
        read.TryGetEdge(importedEdge, out EdgeInfo edgeInfo).Should().BeTrue();
        edgeInfo.Source.Should().Be(document);
        edgeInfo.Target.Should().Be(chunk);
        NexusId importedNexus = read.Query.Nexuses().ToList().Should().ContainSingle().Subject;
        using NexusMemberEnumerator members = read.GetMembers(importedNexus);
        var memberValues = new List<NexusMember>();
        while (members.MoveNext()) memberValues.Add(members.Current);
        memberValues.Select(member => member.VertexId).Should()
            .BeEquivalentTo(new[] { document, chunk });
    }

    [Fact]
    public void Multiple_documents_deduplicate_normalized_definitions_and_notify_once()
    {
        Guid databaseId = Guid.NewGuid();
        string first = CreateOverlapDocument(databaseId, reverse: false);
        string second = CreateOverlapDocument(databaseId, reverse: true);
        var mappings = new List<GraphJsonImportMapping>();

        using var db = QuiverDatabase.CreateInMemory();
        using IWriteTransaction tx = db.BeginWriteTransaction();
        using var firstStream = JsonStream(first);
        using var secondStream = JsonStream(second);
        GraphJsonImportResult result = GraphJsonImporter.Import(
            tx,
            [firstStream, secondStream],
            new GraphJsonImportOptions { MappingSink = mappings.Add });

        result.DocumentCount.Should().Be(2);
        result.VertexCount.Should().Be(2);
        result.EdgeCount.Should().Be(1);
        result.NexusCount.Should().Be(1);
        result.DuplicateEntityCount.Should().Be(4);
        mappings.Should().HaveCount(4);
        mappings.Should().OnlyContain(mapping => mapping.SourceDatabaseId.Value == databaseId);
        mappings.Select(mapping => (mapping.Kind, mapping.SourcePackedId))
            .Should().OnlyHaveUniqueItems();
        tx.Query.Vertices().ToList().Should().HaveCount(2);
        tx.Query.Edges().ToList().Should().ContainSingle();
        tx.Query.Nexuses().ToList().Should().ContainSingle();
        tx.Rollback();
    }

    [Fact]
    public void Same_local_ids_from_different_databases_are_distinct()
    {
        string first = CreateVertexOnlyDocument(Guid.NewGuid(), "A");
        string second = CreateVertexOnlyDocument(Guid.NewGuid(), "A");
        using var db = QuiverDatabase.CreateInMemory();
        using IWriteTransaction tx = db.BeginWriteTransaction();
        using var firstStream = JsonStream(first);
        using var secondStream = JsonStream(second);

        GraphJsonImportResult result = GraphJsonImporter.Import(tx, [firstStream, secondStream]);

        result.VertexCount.Should().Be(2);
        result.DuplicateEntityCount.Should().Be(0);
        tx.Rollback();
    }

    [Fact]
    public void Conflicting_duplicate_leaves_transaction_available_for_caller_rollback()
    {
        Guid databaseId = Guid.NewGuid();
        string first = CreateVertexOnlyDocument(databaseId, "A");
        string conflicting = CreateVertexOnlyDocument(databaseId, "B");
        using var db = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        using (var firstStream = JsonStream(first))
        using (var conflictingStream = JsonStream(conflicting))
        {
            Action act = () => GraphJsonImporter.Import(tx, [firstStream, conflictingStream]);
            act.Should().Throw<GraphJsonImportException>()
                .WithMessage("*定義が文書間で競合*");
            tx.State.Should().Be(TransactionState.Active);
            tx.Rollback();
        }

        using IReadTransaction read = db.BeginReadTransaction();
        read.Query.Vertices().ToList().Should().BeEmpty();
        read.Schema.ListLabels().Should().BeEmpty();
    }

    [Fact]
    public void Missing_reference_is_an_error_and_can_be_rolled_back()
    {
        string json = CreateMissingReferenceDocument(Guid.NewGuid());
        using var db = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        using (var stream = JsonStream(json))
        {
            Action act = () => GraphJsonImporter.Import(tx, stream);
            act.Should().Throw<GraphJsonImportException>()
                .WithMessage("*同じ文書のverticesに定義されていません*");
            tx.State.Should().Be(TransactionState.Active);
            tx.Rollback();
        }
        using IReadTransaction read = db.BeginReadTransaction();
        read.Query.Vertices().ToList().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void Malformed_or_noncanonical_json_is_never_silently_accepted(string json)
    {
        using var db = QuiverDatabase.CreateInMemory();
        using IWriteTransaction tx = db.BeginWriteTransaction();
        using var stream = JsonStream(json);
        Action act = () => GraphJsonImporter.Import(tx, stream);
        act.Should().Throw<GraphJsonImportException>();
        tx.Rollback();
    }

    public static IEnumerable<object[]> MalformedDocuments()
    {
        Guid databaseId = Guid.NewGuid();
        string valid = CreateVertexOnlyDocument(databaseId, "A");
        yield return [valid[..^1]];
        yield return [valid + "{}"];
        yield return [valid.Replace(
            "\"format\":\"quiver-graph\"",
            "\"format\":\"quiver-graph\",\"format\":\"quiver-graph\"",
            StringComparison.Ordinal)];
        yield return [CreateUnknownReservedValueDocument(databaseId)];
        yield return [CreateUnknownReservedValueDocument(databaseId).Replace(
            "\"single\"",
            "\"many\"",
            StringComparison.Ordinal)];
    }

    [Fact]
    public void Import_reads_incrementally_and_does_not_own_stream_or_transaction()
    {
        using var source = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = source.BeginWriteTransaction())
        {
            for (int i = 0; i < 1_000; i++)
            {
                VertexId vertex = tx.CreateVertex("V");
                tx.SetProperty(vertex, "text", PropertyValue.FromString(new string('x', 64)));
            }
            tx.Commit();
        }
        using var output = new MemoryStream();
        GraphJsonExporter.Export(source, output);
        var input = new CountingReadStream(output.ToArray(), 127);

        using var target = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = target.BeginWriteTransaction();
        GraphJsonImportResult result = GraphJsonImporter.Import(write, input);

        result.VertexCount.Should().Be(1_000);
        input.ReadCount.Should().BeGreaterThan(10);
        input.WasDisposed.Should().BeFalse();
        write.State.Should().Be(TransactionState.Active);
        write.Rollback();
        input.Dispose();
        input.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public void Unused_roles_are_replayed_as_transactional_schema()
    {
        string json = CreateVertexOnlyDocument(Guid.NewGuid(), "A", roles: "\"unused\"");
        using var db = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction tx = db.BeginWriteTransaction())
        using (var stream = JsonStream(json))
        {
            GraphJsonImporter.Import(tx, stream);
            tx.Schema.ListRoles().Should().Contain("unused");
            tx.Commit();
        }
        using IReadTransaction read = db.BeginReadTransaction();
        read.Schema.ListRoles().Should().Contain("unused");
    }

    private static string CreateOverlapDocument(Guid databaseId, bool reverse)
    {
        string properties = reverse
            ? """
              {"key":"tags","cardinality":"set","type":"string","value":"b"},
              {"key":"name","cardinality":"single","type":"string","value":"one"},
              {"key":"tags","cardinality":"set","type":"string","value":"a"}
              """
            : """
              {"key":"tags","cardinality":"set","type":"string","value":"a"},
              {"key":"tags","cardinality":"set","type":"string","value":"b"},
              {"key":"name","cardinality":"single","type":"string","value":"one"}
              """;
        string members = reverse
            ? """{"role":"right","vertex":"2"},{"role":"left","vertex":"1"}"""
            : """{"role":"left","vertex":"1"},{"role":"right","vertex":"2"}""";
        return $$"""
        {
          "format":"quiver-graph",
          "version":1,
          "source":{"databaseId":"{{databaseId:D}}","snapshot":"{{Guid.NewGuid():D}}"},
          "schema":{
            "labels":["V"],
            "edgeTypes":["E"],
            "nexusTypes":["N"],
            "roles":["left","right"],
            "propertyKeys":[
              {"name":"name","cardinality":"single"},
              {"name":"tags","cardinality":"set"}
            ]
          },
          "vertices":[
            {"id":"1","label":"V","properties":[{{properties}}]},
            {"id":"2","label":"V","properties":[]}
          ],
          "edges":[{"id":"3","type":"E","source":"1","target":"2","properties":[]}],
          "nexuses":[{"id":"4","type":"N","members":[{{members}}],"properties":[]}]
        }
        """;
    }

    private static string CreateVertexOnlyDocument(
        Guid databaseId,
        string label,
        string roles = "")
        => $$"""
        {
          "format":"quiver-graph",
          "version":1,
          "source":{"databaseId":"{{databaseId:D}}","snapshot":"{{Guid.NewGuid():D}}"},
          "schema":{
            "labels":["{{label}}"],
            "edgeTypes":[],
            "nexusTypes":[],
            "roles":[{{roles}}],
            "propertyKeys":[]
          },
          "vertices":[{"id":"1","label":"{{label}}","properties":[]}],
          "edges":[],
          "nexuses":[]
        }
        """;

    private static string CreateMissingReferenceDocument(Guid databaseId)
        => $$"""
        {
          "format":"quiver-graph",
          "version":1,
          "source":{"databaseId":"{{databaseId:D}}","snapshot":"{{Guid.NewGuid():D}}"},
          "schema":{
            "labels":["V"],"edgeTypes":["E"],"nexusTypes":[],"roles":[],"propertyKeys":[]
          },
          "vertices":[{"id":"1","label":"V","properties":[]}],
          "edges":[{"id":"2","type":"E","source":"1","target":"99","properties":[]}],
          "nexuses":[]
        }
        """;

    private static string CreateUnknownReservedValueDocument(Guid databaseId)
        => $$"""
        {
          "format":"quiver-graph",
          "version":1,
          "source":{"databaseId":"{{databaseId:D}}","snapshot":"{{Guid.NewGuid():D}}"},
          "schema":{
            "labels":["V"],"edgeTypes":[],"nexusTypes":[],"roles":[],
            "propertyKeys":[{"name":"score","cardinality":"single"}]
          },
          "vertices":[{"id":"1","label":"V","properties":[
            {"key":"score","cardinality":"single","type":"double","value":"positiveInfinity"}
          ]}],
          "edges":[],
          "nexuses":[]
        }
        """;

    private static MemoryStream JsonStream(string json)
        => new(Encoding.UTF8.GetBytes(json));

    private sealed class CountingReadStream(byte[] content, int maxRead) : MemoryStream(content)
    {
        internal int ReadCount { get; private set; }
        internal bool WasDisposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return base.Read(buffer, offset, Math.Min(count, maxRead));
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}

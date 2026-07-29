using System.Text;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class GraphJsonSemanticMergeTests
{
    [Theory]
    [InlineData(GraphJsonPropertyConflictPolicy.KeepTarget, "target")]
    [InlineData(GraphJsonPropertyConflictPolicy.OverwriteTarget, "source")]
    public void Property_policy_applies_to_vertex_edge_and_nexus(
        GraphJsonPropertyConflictPolicy policy,
        string expected)
    {
        using var database = QuiverDatabase.CreateInMemory();
        SeedTarget(database, duplicateVertices: false, duplicateEdges: false, duplicateNexuses: false);
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var document = JsonStream(CreateGraphDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "source"));

        GraphJsonImportResult result = GraphJsonImporter.Import(
            write,
            document,
            Options(policy));
        result.VertexCount.Should().Be(0);
        result.EdgeCount.Should().Be(0);
        result.NexusCount.Should().Be(0);
        result.SemanticMatchCount.Should().Be(4);
        write.Commit();

        using IReadTransaction read = database.BeginReadTransaction();
        VertexId first = FindPerson(read, "p1");
        ReadString(read.GetProperty(first, "note")).Should().Be(expected);
        ReadStrings(read.GetPropertyValues(first, "tags"))
            .Should().BeEquivalentTo("common", expected);
        EdgeId edge = read.Query.Edges().ToList().Should().ContainSingle().Subject;
        ReadString(read.GetProperty(edge, "note")).Should().Be(expected);
        ReadStrings(read.GetPropertyValues(edge, "tags"))
            .Should().BeEquivalentTo("common", expected);
        NexusId nexus = read.Query.Nexuses().ToList().Should().ContainSingle().Subject;
        ReadString(read.GetProperty(nexus, "note")).Should().Be(expected);
        ReadStrings(read.GetPropertyValues(nexus, "tags"))
            .Should().BeEquivalentTo("common", expected);
        ReadString(read.GetProperty(first, "externalId")).Should().Be("p1");
    }

    [Fact]
    public void Error_policy_reports_property_conflict_and_caller_can_rollback()
    {
        using var database = QuiverDatabase.CreateInMemory();
        SeedTarget(database, duplicateVertices: false, duplicateEdges: false, duplicateNexuses: false);
        using (IWriteTransaction write = database.BeginWriteTransaction())
        using (var document = JsonStream(CreateGraphDocument(Guid.NewGuid(), "source")))
        {
            Action import = () => GraphJsonImporter.Import(
                write,
                document,
                Options(GraphJsonPropertyConflictPolicy.Error));
            import.Should().Throw<GraphJsonImportException>()
                .WithMessage("*property 'note'*競合*");
            write.State.Should().Be(Transactions.TransactionState.Active);
            write.Rollback();
        }

        using IReadTransaction read = database.BeginReadTransaction();
        ReadString(read.GetProperty(FindPerson(read, "p1"), "note")).Should().Be("target");
        read.Query.Edges().ToList().Should().ContainSingle();
        read.Query.Nexuses().ToList().Should().ContainSingle();
    }

    [Fact]
    public void Deferred_overwrite_is_independent_of_document_order_for_all_entity_kinds()
    {
        string first = CreateGraphDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "first");
        string second = CreateGraphDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "second");

        string[] forward = ImportAndReadValues(first, second);
        string[] reverse = ImportAndReadValues(second, first);

        forward.Should().Equal("second", "second", "second");
        reverse.Should().Equal(forward);
    }

    [Fact]
    public void Multiple_vertex_candidates_are_rejected_instead_of_using_MergeVertex()
    {
        using var database = QuiverDatabase.CreateInMemory();
        SeedTarget(database, duplicateVertices: true, duplicateEdges: false, duplicateNexuses: false);
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var document = JsonStream(CreateGraphDocument(Guid.NewGuid(), "target"));

        Action import = () => GraphJsonImporter.Import(
            write,
            document,
            Options(GraphJsonPropertyConflictPolicy.KeepTarget));

        import.Should().Throw<GraphJsonImportException>()
            .WithMessage("*target候補が複数*");
        write.Rollback();
    }

    [Fact]
    public void Multiple_edge_candidates_are_rejected_instead_of_arbitrary_selection()
    {
        using var database = QuiverDatabase.CreateInMemory();
        SeedTarget(database, duplicateVertices: false, duplicateEdges: true, duplicateNexuses: false);
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var document = JsonStream(CreateGraphDocument(Guid.NewGuid(), "target"));

        Action import = () => GraphJsonImporter.Import(
            write,
            document,
            Options(GraphJsonPropertyConflictPolicy.KeepTarget));

        import.Should().Throw<GraphJsonImportException>()
            .WithMessage("*Edge*target候補が複数*");
        write.Rollback();
    }

    [Fact]
    public void Multiple_nexus_candidates_are_rejected_instead_of_arbitrary_selection()
    {
        using var database = QuiverDatabase.CreateInMemory();
        SeedTarget(database, duplicateVertices: false, duplicateEdges: false, duplicateNexuses: true);
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var document = JsonStream(CreateGraphDocument(Guid.NewGuid(), "target"));

        Action import = () => GraphJsonImporter.Import(
            write,
            document,
            Options(GraphJsonPropertyConflictPolicy.KeepTarget));

        import.Should().Throw<GraphJsonImportException>()
            .WithMessage("*Nexus*target候補が複数*");
        write.Rollback();
    }

    [Fact]
    public void Missing_or_non_single_identity_property_is_rejected()
    {
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        string missing = CreateVertexDocument(
            Guid.NewGuid(),
            """{"name":"externalId","cardinality":"single"}""",
            "");
        using var document = JsonStream(missing);

        Action import = () => GraphJsonImporter.Import(
            write,
            document,
            Options(GraphJsonPropertyConflictPolicy.Error));

        import.Should().Throw<GraphJsonImportException>()
            .WithMessage("*identity property*Single値を一つ*");
        write.Rollback();
    }

    [Fact]
    public void Set_cardinality_identity_property_is_rejected()
    {
        string setIdentity = CreateVertexDocument(
            Guid.NewGuid(),
            """{"name":"externalId","cardinality":"set"}""",
            """{"key":"externalId","cardinality":"set","type":"string","value":"p1"}""");
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var document = JsonStream(setIdentity);

        Action import = () => GraphJsonImporter.Import(
            write,
            document,
            Options(GraphJsonPropertyConflictPolicy.Error));

        import.Should().Throw<GraphJsonImportException>()
            .WithMessage("*identity property*Single値を一つ*");
        write.Rollback();
    }

    [Fact]
    public void Existing_target_identity_physical_type_mismatch_is_rejected()
    {
        using var database = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction seed = database.BeginWriteTransaction())
        {
            VertexId target = seed.CreateVertex("Person");
            seed.SetProperty(target, "externalId", PropertyValue.FromInt32(1));
            seed.Commit();
        }
        string source = CreateVertexDocument(
            Guid.NewGuid(),
            """{"name":"externalId","cardinality":"single"}""",
            """{"key":"externalId","cardinality":"single","type":"string","value":"1"}""");
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var document = JsonStream(source);

        Action import = () => GraphJsonImporter.Import(
            write,
            document,
            Options(GraphJsonPropertyConflictPolicy.Error));

        import.Should().Throw<GraphJsonImportException>()
            .WithMessage("*target Vertex*物理型が一致しません*");
        write.Rollback();
    }

    [Fact]
    public void Identity_physical_type_conflict_across_sources_is_rejected()
    {
        string text = CreateVertexDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            """{"name":"externalId","cardinality":"single"}""",
            """{"key":"externalId","cardinality":"single","type":"string","value":"1"}""");
        string integer = CreateVertexDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            """{"name":"externalId","cardinality":"single"}""",
            """{"key":"externalId","cardinality":"single","type":"int32","value":1}""");
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var first = JsonStream(text);
        using var second = JsonStream(integer);

        Action import = () => GraphJsonImporter.Import(
            write,
            [first, second],
            Options(GraphJsonPropertyConflictPolicy.Error));

        import.Should().Throw<GraphJsonImportException>()
            .WithMessage("*identity property*物理型*");
        write.Rollback();
    }

    [Fact]
    public void Semantic_vertex_collapse_that_duplicates_a_nexus_member_is_rejected()
    {
        string documentText = CreateCollapsedNexusDocument(Guid.NewGuid());
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var document = JsonStream(documentText);

        Action import = () => GraphJsonImporter.Import(
            write,
            document,
            Options(GraphJsonPropertyConflictPolicy.Error));

        import.Should().Throw<GraphJsonImportException>()
            .WithMessage("*Duplicate*");
        write.Rollback();
    }

    [Fact]
    public void Empty_target_collapses_cross_database_vertices_and_reports_mapping_and_counts()
    {
        string first = CreateGraphDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "same");
        string second = CreateGraphDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "same",
            reverseMemberOrder: true);
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var firstStream = JsonStream(first);
        using var secondStream = JsonStream(second);
        var mappings = new List<GraphJsonImportMapping>();
        GraphJsonImportOptions options = Options(GraphJsonPropertyConflictPolicy.Error);
        options.MappingSink = mappings.Add;

        GraphJsonImportResult result = GraphJsonImporter.Import(
            write,
            [firstStream, secondStream],
            options);

        result.VertexCount.Should().Be(2);
        result.EdgeCount.Should().Be(1);
        result.NexusCount.Should().Be(1);
        result.SemanticMatchCount.Should().Be(4);
        result.DuplicateEntityCount.Should().Be(0);
        mappings.Should().HaveCount(8);
        mappings.Select(mapping => mapping.SourceDatabaseId).Distinct().Should().HaveCount(2);
        mappings.Where(mapping => mapping.Kind == EntityKind.Vertex)
            .Select(mapping => mapping.ProvisionalTargetPackedId).Distinct().Should().HaveCount(2);
        mappings.Where(mapping => mapping.Kind == EntityKind.Edge)
            .Select(mapping => mapping.ProvisionalTargetPackedId).Distinct().Should().ContainSingle();
        mappings.Where(mapping => mapping.Kind == EntityKind.Nexus)
            .Select(mapping => mapping.ProvisionalTargetPackedId).Distinct().Should().ContainSingle();
        write.Commit();

        using IReadTransaction read = database.BeginReadTransaction();
        read.Query.Vertices().ToList().Should().HaveCount(2);
        read.Query.Edges().ToList().Should().ContainSingle();
        read.Query.Nexuses().ToList().Should().ContainSingle();
    }

    [Fact]
    public void Same_sequence_with_different_source_generations_remains_distinct_before_semantic_reuse()
    {
        Guid databaseId = Guid.NewGuid();
        string generationZero = CreateVertexDocument(
            databaseId,
            """{"name":"externalId","cardinality":"single"}""",
            """{"key":"externalId","cardinality":"single","type":"string","value":"shared"}""",
            id: "1");
        string generationOne = CreateVertexDocument(
            databaseId,
            """{"name":"externalId","cardinality":"single"}""",
            """{"key":"externalId","cardinality":"single","type":"string","value":"shared"}""",
            id: VertexId.Create(1, 1).Value.ToString());
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var firstStream = JsonStream(generationZero);
        using var secondStream = JsonStream(generationOne);
        var mappings = new List<GraphJsonImportMapping>();
        GraphJsonImportOptions options = Options(GraphJsonPropertyConflictPolicy.Error);
        options.MappingSink = mappings.Add;

        GraphJsonImportResult result = GraphJsonImporter.Import(
            write,
            [firstStream, secondStream],
            options);

        result.VertexCount.Should().Be(1);
        result.SemanticMatchCount.Should().Be(1);
        result.DuplicateEntityCount.Should().Be(0);
        mappings.Select(mapping => mapping.SourcePackedId).Should().OnlyHaveUniqueItems();
        mappings.Select(mapping => mapping.ProvisionalTargetPackedId)
            .Distinct().Should().ContainSingle();
        write.Rollback();
    }

    [Fact]
    public void Same_source_duplicate_is_deduplicated_before_semantic_matching()
    {
        string json = CreateGraphDocument(Guid.NewGuid(), "same");
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var first = JsonStream(json);
        using var duplicate = JsonStream(json);

        GraphJsonImportResult result = GraphJsonImporter.Import(
            write,
            [first, duplicate],
            Options(GraphJsonPropertyConflictPolicy.Error));

        result.DocumentCount.Should().Be(2);
        result.VertexCount.Should().Be(2);
        result.EdgeCount.Should().Be(1);
        result.NexusCount.Should().Be(1);
        result.DuplicateEntityCount.Should().Be(4);
        result.SemanticMatchCount.Should().Be(0);
        write.Rollback();
    }

    [Fact]
    public void KeepTarget_result_is_independent_of_document_order_on_empty_target()
    {
        string lower = CreateVertexWithNoteDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "lower");
        string higher = CreateVertexWithNoteDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "higher");

        ImportVertexNote(GraphJsonPropertyConflictPolicy.KeepTarget, lower, higher)
            .Should().Be("lower");
        ImportVertexNote(GraphJsonPropertyConflictPolicy.KeepTarget, higher, lower)
            .Should().Be("lower");
    }

    [Fact]
    public void Error_result_is_independent_of_document_order_on_empty_target()
    {
        string lower = CreateVertexWithNoteDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "lower");
        string higher = CreateVertexWithNoteDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "higher");

        foreach (string[] order in new[] { new[] { lower, higher }, new[] { higher, lower } })
        {
            using var database = QuiverDatabase.CreateInMemory();
            using IWriteTransaction write = database.BeginWriteTransaction();
            using var first = JsonStream(order[0]);
            using var second = JsonStream(order[1]);

            Action import = () => GraphJsonImporter.Import(
                write,
                [first, second],
                Options(GraphJsonPropertyConflictPolicy.Error));

            import.Should().Throw<GraphJsonImportException>()
                .WithMessage("*property 'note'*競合*");
            write.State.Should().Be(Transactions.TransactionState.Active);
            write.Rollback();
        }
    }

    [Fact]
    public void Different_relation_shapes_are_not_semantically_reused()
    {
        string forward = CreateGraphDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "same");
        string reversed = CreateGraphDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "same",
            edgeSource: "2",
            edgeTarget: "1",
            leftMember: "2",
            rightMember: "1");
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var first = JsonStream(forward);
        using var second = JsonStream(reversed);

        GraphJsonImportResult result = GraphJsonImporter.Import(
            write,
            [first, second],
            Options(GraphJsonPropertyConflictPolicy.KeepTarget));

        result.VertexCount.Should().Be(2);
        result.EdgeCount.Should().Be(2);
        result.NexusCount.Should().Be(2);
        result.SemanticMatchCount.Should().Be(2);
        write.Rollback();
    }

    [Fact]
    public void Rollback_removes_entities_and_properties_applied_before_later_conflict()
    {
        using var database = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction seed = database.BeginWriteTransaction())
        {
            CreatePerson(seed, "existing", "target");
            seed.Commit();
        }
        string earlierCreation = CreateVertexWithNoteDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "applied",
            externalId: "created");
        string laterConflict = CreateVertexWithNoteDocument(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "source",
            externalId: "existing");
        using (IWriteTransaction write = database.BeginWriteTransaction())
        using (var first = JsonStream(earlierCreation))
        using (var second = JsonStream(laterConflict))
        {
            Action import = () => GraphJsonImporter.Import(
                write,
                [first, second],
                Options(GraphJsonPropertyConflictPolicy.Error));

            import.Should().Throw<GraphJsonImportException>();
            VertexId partiallyApplied = FindPerson(write, "created");
            ReadString(write.GetProperty(partiallyApplied, "note")).Should().Be("applied");
            write.Rollback();
        }

        using IReadTransaction read = database.BeginReadTransaction();
        read.Query.Vertices().HasLabel("Person").ToList().Should().ContainSingle();
        ReadString(read.GetProperty(FindPerson(read, "existing"), "note"))
            .Should().Be("target");
    }

    [Fact]
    public void Cancellation_before_semantic_completion_propagates_and_caller_can_rollback()
    {
        string json = CreateVertexWithNoteDocument(Guid.NewGuid(), "deferred");
        using var database = QuiverDatabase.CreateInMemory();
        using IWriteTransaction write = database.BeginWriteTransaction();
        using var document = JsonStream(json);
        using var cancellation = new CancellationTokenSource();

        Action import = () => GraphJsonImporter.Import(
            write,
            CancelBeforeSemanticCompletion(document, cancellation),
            Options(GraphJsonPropertyConflictPolicy.Error),
            cancellation.Token);

        import.Should().Throw<OperationCanceledException>();
        write.State.Should().Be(Transactions.TransactionState.Active);
        VertexId provisional = FindPerson(write, "shared");
        write.HasProperty(provisional, "note").Should().BeFalse();
        write.Rollback();

        using IReadTransaction read = database.BeginReadTransaction();
        read.Query.Vertices().ToList().Should().BeEmpty();
    }

    private static string[] ImportAndReadValues(params string[] documents)
    {
        using var database = QuiverDatabase.CreateInMemory();
        SeedTarget(database, duplicateVertices: false, duplicateEdges: false, duplicateNexuses: false);
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            MemoryStream[] streams = documents.Select(JsonStream).ToArray();
            try
            {
                GraphJsonImporter.Import(
                    write,
                    streams,
                    Options(GraphJsonPropertyConflictPolicy.OverwriteTarget));
                write.Commit();
            }
            finally
            {
                foreach (MemoryStream stream in streams)
                    stream.Dispose();
            }
        }

        using IReadTransaction read = database.BeginReadTransaction();
        VertexId first = FindPerson(read, "p1");
        EdgeId edge = read.Query.Edges().ToList().Single();
        NexusId nexus = read.Query.Nexuses().ToList().Single();
        return [
            ReadString(read.GetProperty(first, "note")),
            ReadString(read.GetProperty(edge, "note")),
            ReadString(read.GetProperty(nexus, "note")),
        ];
    }

    private static string ImportVertexNote(
        GraphJsonPropertyConflictPolicy policy,
        params string[] documents)
    {
        using var database = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            MemoryStream[] streams = documents.Select(JsonStream).ToArray();
            try
            {
                GraphJsonImporter.Import(write, streams, Options(policy));
                write.Commit();
            }
            finally
            {
                foreach (MemoryStream stream in streams)
                    stream.Dispose();
            }
        }

        using IReadTransaction read = database.BeginReadTransaction();
        VertexId vertex = read.Query.Vertices().HasLabel("Person").ToList().Single();
        return ReadString(read.GetProperty(vertex, "note"));
    }

    private static GraphJsonImportOptions Options(GraphJsonPropertyConflictPolicy policy)
        => new()
        {
            SemanticMerge = new GraphJsonSemanticMergeOptions
            {
                VertexIdentityRules = [new("Person", "externalId")],
                PropertyConflictPolicy = policy,
            },
        };

    private static void SeedTarget(
        QuiverDatabase database,
        bool duplicateVertices,
        bool duplicateEdges,
        bool duplicateNexuses)
    {
        using IWriteTransaction write = database.BeginWriteTransaction();
        VertexId first = CreatePerson(write, "p1", "target");
        VertexId second = CreatePerson(write, "p2", null);
        if (duplicateVertices)
            CreatePerson(write, "p1", "duplicate");
        int edgeCount = duplicateEdges ? 2 : 1;
        for (int i = 0; i < edgeCount; i++)
        {
            EdgeId edge = write.CreateEdge(first, second, "KNOWS");
            write.SetProperty(edge, "note", PropertyValue.FromString("target"));
            write.AddPropertyValue(edge, "tags", PropertyValue.FromString("common"));
            write.AddPropertyValue(edge, "tags", PropertyValue.FromString("target"));
        }
        int nexusCount = duplicateNexuses ? 2 : 1;
        for (int i = 0; i < nexusCount; i++)
        {
            NexusId nexus = write.CreateNexus("Group", [new("left", first), new("right", second)]);
            write.SetProperty(nexus, "note", PropertyValue.FromString("target"));
            write.AddPropertyValue(nexus, "tags", PropertyValue.FromString("common"));
            write.AddPropertyValue(nexus, "tags", PropertyValue.FromString("target"));
        }
        write.Commit();
    }

    private static VertexId CreatePerson(IWriteTransaction write, string externalId, string? note)
    {
        VertexId vertex = write.CreateVertex("Person");
        write.SetProperty(vertex, "externalId", PropertyValue.FromString(externalId));
        if (note is not null)
        {
            write.SetProperty(vertex, "note", PropertyValue.FromString(note));
            write.AddPropertyValue(vertex, "tags", PropertyValue.FromString("common"));
            write.AddPropertyValue(vertex, "tags", PropertyValue.FromString(note));
        }
        return vertex;
    }

    private static VertexId FindPerson(IReadTransaction read, string externalId)
        => read.Query.Vertices().HasLabel("Person").ToList()
            .Single(vertex => ReadString(read.GetProperty(vertex, "externalId")) == externalId);

    private static string ReadString(PropertyValue value)
        => Encoding.UTF8.GetString(value.Utf8StringValue);

    private static List<string> ReadStrings(PropertyValuesEnumerator values)
    {
        var result = new List<string>();
        while (values.MoveNext())
            result.Add(ReadString(values.Current));
        values.Dispose();
        return result;
    }

    private static MemoryStream JsonStream(string json)
        => new(Encoding.UTF8.GetBytes(json));

    private static IEnumerable<Stream> CancelBeforeSemanticCompletion(
        Stream document,
        CancellationTokenSource cancellation)
    {
        yield return document;
        cancellation.Cancel();
    }

    private static string CreateGraphDocument(
        Guid databaseId,
        string note,
        string edgeSource = "1",
        string edgeTarget = "2",
        string leftMember = "1",
        string rightMember = "2",
        bool reverseMemberOrder = false)
    {
        string members = reverseMemberOrder
            ? $$"""{"role":"right","vertex":"{{rightMember}}"},{"role":"left","vertex":"{{leftMember}}"}"""
            : $$"""{"role":"left","vertex":"{{leftMember}}"},{"role":"right","vertex":"{{rightMember}}"}""";
        return $$"""
        {
          "format":"quiver-graph","version":1,
          "source":{"databaseId":"{{databaseId:D}}","snapshot":"{{Guid.NewGuid():D}}"},
          "schema":{
            "labels":["Person"],"edgeTypes":["KNOWS"],"nexusTypes":["Group"],
            "roles":["left","right"],
            "propertyKeys":[
              {"name":"externalId","cardinality":"single"},
              {"name":"note","cardinality":"single"},
              {"name":"tags","cardinality":"set"}
            ]
          },
          "vertices":[
            {"id":"1","label":"Person","properties":[
              {"key":"externalId","cardinality":"single","type":"string","value":"p1"},
              {"key":"note","cardinality":"single","type":"string","value":"{{note}}"},
              {"key":"tags","cardinality":"set","type":"string","value":"common"},
              {"key":"tags","cardinality":"set","type":"string","value":"{{note}}"}
            ]},
            {"id":"2","label":"Person","properties":[
              {"key":"externalId","cardinality":"single","type":"string","value":"p2"}
            ]}
          ],
          "edges":[{"id":"3","type":"KNOWS","source":"{{edgeSource}}","target":"{{edgeTarget}}","properties":[
            {"key":"note","cardinality":"single","type":"string","value":"{{note}}"},
            {"key":"tags","cardinality":"set","type":"string","value":"common"},
            {"key":"tags","cardinality":"set","type":"string","value":"{{note}}"}
          ]}],
          "nexuses":[{"id":"4","type":"Group","members":[{{members}}],"properties":[
            {"key":"note","cardinality":"single","type":"string","value":"{{note}}"},
            {"key":"tags","cardinality":"set","type":"string","value":"common"},
            {"key":"tags","cardinality":"set","type":"string","value":"{{note}}"}
          ]}]
        }
        """;
    }

    private static string CreateVertexWithNoteDocument(
        Guid databaseId,
        string note,
        string externalId = "shared")
        => CreateVertexDocument(
            databaseId,
            """{"name":"externalId","cardinality":"single"},{"name":"note","cardinality":"single"}""",
            $$"""{"key":"externalId","cardinality":"single","type":"string","value":"{{externalId}}"},{"key":"note","cardinality":"single","type":"string","value":"{{note}}"}""");

    private static string CreateVertexDocument(
        Guid databaseId,
        string propertySchema,
        string properties,
        string id = "1")
        => $$"""
        {
          "format":"quiver-graph","version":1,
          "source":{"databaseId":"{{databaseId:D}}","snapshot":"{{Guid.NewGuid():D}}"},
          "schema":{"labels":["Person"],"edgeTypes":[],"nexusTypes":[],"roles":[],
            "propertyKeys":[{{propertySchema}}]},
          "vertices":[{"id":"{{id}}","label":"Person","properties":[{{properties}}]}],
          "edges":[],"nexuses":[]
        }
        """;

    private static string CreateCollapsedNexusDocument(Guid databaseId)
        => $$"""
        {
          "format":"quiver-graph","version":1,
          "source":{"databaseId":"{{databaseId:D}}","snapshot":"{{Guid.NewGuid():D}}"},
          "schema":{"labels":["Person"],"edgeTypes":[],"nexusTypes":["Group"],"roles":["same"],
            "propertyKeys":[{"name":"externalId","cardinality":"single"}]},
          "vertices":[
            {"id":"1","label":"Person","properties":[
              {"key":"externalId","cardinality":"single","type":"string","value":"p1"}]},
            {"id":"2","label":"Person","properties":[
              {"key":"externalId","cardinality":"single","type":"string","value":"p1"}]}
          ],
          "edges":[],
          "nexuses":[{"id":"3","type":"Group","members":[
            {"role":"same","vertex":"1"},{"role":"same","vertex":"2"}],"properties":[]}]
        }
        """;
}

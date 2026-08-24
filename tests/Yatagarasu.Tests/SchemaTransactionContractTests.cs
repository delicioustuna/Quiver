using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class SchemaTransactionContractTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "yatagarasu_schema_transaction_" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SchemaTransactionContractTests()
    {
        _path = Path.Combine(_directory, "graph.yata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Uncommitted_schema_is_hidden_and_rollback_removes_it()
    {
        using var database = YatagarasuDatabase.Open(_path);
        using (var write = database.BeginWriteTransaction())
        {
            write.EditSchema.GetOrCreateLabel("Pending");
            write.EditSchema.GetOrCreatePropertyKey("pendingKey");
            write.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_pending",
                new PropertyTarget(PropertyOwnerKind.Vertex, "pendingKey", "Pending"),
                IndexKind.StringEquality));

            using var concurrentReader = database.BeginReadTransaction();
            concurrentReader.Schema.TryGetLabelId("Pending", out _).Should().BeFalse();
            concurrentReader.Schema.TryGetPropertyKeyId("pendingKey", out _).Should().BeFalse();
            concurrentReader.Schema.IndexExists("idx_pending").Should().BeFalse();
            write.Rollback();
        }

        database.Schema.TryGetLabelId("Pending", out _).Should().BeFalse();
        database.Schema.TryGetPropertyKeyId("pendingKey", out _).Should().BeFalse();
        database.Schema.IndexExists("idx_pending").Should().BeFalse();
    }

    [Fact]
    public void Read_schema_catalog_remains_at_its_opening_snapshot()
    {
        using var database = YatagarasuDatabase.Open(_path);
        using var oldReader = database.BeginReadTransaction();

        using (var write = database.BeginWriteTransaction())
        {
            write.EditSchema.GetOrCreateLabel("CommittedLater");
            write.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_later",
                new PropertyTarget(PropertyOwnerKind.Vertex, "name", "CommittedLater"),
                IndexKind.StringEquality));
            write.Commit();
        }

        oldReader.Schema.TryGetLabelId("CommittedLater", out _).Should().BeFalse();
        oldReader.Schema.IndexExists("idx_later").Should().BeFalse();
        using var newReader = database.BeginReadTransaction();
        newReader.Schema.TryGetLabelId("CommittedLater", out _).Should().BeTrue();
        newReader.Schema.IndexExists("idx_later").Should().BeTrue();
    }

    [Fact]
    public void Savepoint_rollback_restores_schema_catalog_before_commit_and_reopen()
    {
        using (var database = YatagarasuDatabase.Open(_path))
        using (var write = database.BeginWriteTransaction())
        {
            write.EditSchema.GetOrCreateLabel("Kept");
            SavepointId savepoint = write.Savepoint("schema");
            write.EditSchema.GetOrCreateLabel("Removed");
            write.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_removed",
                new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Removed"),
                IndexKind.StringEquality));

            write.RollbackTo(savepoint);
            write.EditSchema.TryGetLabelId("Removed", out _).Should().BeFalse();
            write.EditSchema.IndexExists("idx_removed").Should().BeFalse();
            write.Commit();
        }

        using var reopened = YatagarasuDatabase.Open(_path);
        reopened.Schema.TryGetLabelId("Kept", out _).Should().BeTrue();
        reopened.Schema.TryGetLabelId("Removed", out _).Should().BeFalse();
        reopened.Schema.IndexExists("idx_removed").Should().BeFalse();
    }

    [Fact]
    public void Index_definition_rename_drop_and_target_changes_follow_transaction_boundaries()
    {
        using (var database = YatagarasuDatabase.Open(_path))
        {
            using (var create = database.BeginWriteTransaction())
            {
                create.EditSchema.GetOrCreateLabel("Person");
                create.EditSchema.GetOrCreatePropertyKey("name");
                create.EditSchema.CreateIndex(new ScalarIndexDefinition(
                    "idx_person_name",
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        "name",
                        "Person"),
                    IndexKind.StringEquality));
                create.Commit();
            }

            using (var aborted = database.BeginWriteTransaction())
            {
                aborted.EditSchema.RenameLabel("Person", "Human")
                    .Should().BeTrue();
                aborted.EditSchema.RenamePropertyKey("name", "displayName")
                    .Should().BeTrue();
                aborted.EditSchema.RenameIndex(
                    "idx_person_name",
                    "idx_human_name").Should().BeTrue();
                aborted.Rollback();
            }

            IndexInfo original = database.Schema.ListIndexes().Single();
            original.Name.Should().Be("idx_person_name");
            original.Target.Should().Be(new PropertyTarget(
                PropertyOwnerKind.Vertex,
                "name",
                "Person"));

            using (var renamed = database.BeginWriteTransaction())
            {
                renamed.EditSchema.RenameLabel("Person", "Human")
                    .Should().BeTrue();
                renamed.EditSchema.RenamePropertyKey("name", "displayName")
                    .Should().BeTrue();
                renamed.EditSchema.RenameIndex(
                    "idx_person_name",
                    "idx_human_name").Should().BeTrue();
                renamed.Commit();
            }

            IndexInfo current = database.Schema.ListIndexes().Single();
            current.Name.Should().Be("idx_human_name");
            current.Target.Should().Be(new PropertyTarget(
                PropertyOwnerKind.Vertex,
                "displayName",
                "Human"));
        }

        using (var reopened = YatagarasuDatabase.Open(_path))
        {
            IndexInfo persisted = reopened.Schema.ListIndexes().Single();
            persisted.Name.Should().Be("idx_human_name");
            persisted.State.Should().Be(IndexLifecycleState.Ready);
            persisted.Target.Should().Be(new PropertyTarget(
                PropertyOwnerKind.Vertex,
                "displayName",
                "Human"));

            using var drop = reopened.BeginWriteTransaction();
            drop.EditSchema.DropIndex("idx_human_name");
            drop.Commit();
        }

        using var afterDrop = YatagarasuDatabase.Open(_path);
        afterDrop.Schema.IndexExists("idx_human_name").Should().BeFalse();
    }

    [Fact]
    public void Unknown_read_tokens_return_empty_without_mutating_catalogs()
    {
        string controlPath = Path.Combine(_directory, "control.yata");
        (VertexId left, NexusId nexus) = SeedKnownGraph(_path);
        File.Copy(_path, controlPath);

        using (var database = YatagarasuDatabase.Open(_path))
        {
            string[] labels = [.. database.Schema.ListLabels()];
            string[] edgeTypes = [.. database.Schema.ListEdgeTypes()];
            string[] propertyKeys = [.. database.Schema.ListPropertyKeys()];
            string[] nexusTypes = [.. database.Schema.ListNexusTypes()];
            string[] roles = [.. database.Schema.ListRoles()];
            IndexInfo[] indexes = [.. database.Schema.ListIndexes()];

            using (var read = database.BeginReadTransaction())
            {
                read.Query.Vertices().HasLabel("MissingLabel").ToList()
                    .Should().BeEmpty();
                using EdgeEnumerator edges = read.EnumerateEdges(
                    left,
                    typeFilter: "MissingEdge");
                edges.MoveNext().Should().BeFalse();
                NexusMemberEnumerator members = read.GetMembers(
                    nexus,
                    "MissingRole");
                members.MoveNext().Should().BeFalse();
                NexusIdEnumerator nexuses = read.GetNexuses(
                    left,
                    "MissingNexus",
                    "MissingRole");
                nexuses.MoveNext().Should().BeFalse();
                read.GetProperty(left, "MissingProperty").Type
                    .Should().Be((PropertyValueType)0);
            }

            database.Schema.ListLabels().Should().Equal(labels);
            database.Schema.ListEdgeTypes().Should().Equal(edgeTypes);
            database.Schema.ListPropertyKeys().Should().Equal(propertyKeys);
            database.Schema.ListNexusTypes().Should().Equal(nexusTypes);
            database.Schema.ListRoles().Should().Equal(roles);
            database.Schema.ListIndexes().Should().Equal(indexes);
        }

        using (var control = YatagarasuDatabase.Open(controlPath))
        using (control.BeginReadTransaction())
        {
        }

        File.ReadAllBytes(_path).Should().Equal(File.ReadAllBytes(controlPath));
        ReadFileOrEmpty(_path + "-wal")
            .Should().Equal(ReadFileOrEmpty(controlPath + "-wal"));
    }

    private static byte[] ReadFileOrEmpty(string path)
        => File.Exists(path) ? File.ReadAllBytes(path) : [];

    private static (VertexId Left, NexusId Nexus) SeedKnownGraph(string path)
    {
        using var database = YatagarasuDatabase.Open(path);
        using var seed = database.BeginWriteTransaction();
        VertexId left = seed.CreateVertex("Known");
        VertexId right = seed.CreateVertex("Known");
        seed.CreateEdge(left, right, "KNOWN_EDGE");
        NexusId nexus = seed.CreateNexus(
            "KnownNexus",
            [new("left", left), new("right", right)]);
        seed.Commit();
        return (left, nexus);
    }
}

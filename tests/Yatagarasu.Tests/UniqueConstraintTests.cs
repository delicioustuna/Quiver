using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Index;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class UniqueConstraintTests : IDisposable
{
    private const string IndexName = "idx_person_code";
    private readonly string _directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "yatagarasu_unique_" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => System.IO.Path.Combine(_directory, "graph.yata");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Duplicate_write_is_rejected_before_mutation()
    {
        using var database = YatagarasuDatabase.Open(DatabasePath);
        VertexId first;
        using (var seed = database.BeginWriteTransaction())
        {
            CreateUniqueIndex(seed);
            first = seed.CreateVertex("Person");
            seed.SetProperty(first, "code", PropertyValue.FromString("A-001"));
            seed.Commit();
        }

        using (var write = database.BeginWriteTransaction())
        {
            write.SetProperty(first, "code", PropertyValue.FromString("A-001"));
            VertexId second = write.CreateVertex("Person");
            Action duplicate = () => write.SetProperty(
                second,
                "code",
                PropertyValue.FromString("A-001"));

            UniqueConstraintViolationException error = duplicate.Should()
                .Throw<UniqueConstraintViolationException>()
                .Which;
            error.IndexName.Should().Be(IndexName);
            error.Scope.Should().Be("Person");
            error.PropertyKey.Should().Be("code");
            write.HasProperty(second, "code").Should().BeFalse();

            write.SetProperty(second, "code", PropertyValue.FromString("A-002"));
            write.Commit();
        }
    }

    [Fact]
    public void Remove_and_delete_release_a_value_in_the_same_transaction()
    {
        using var database = YatagarasuDatabase.Open(DatabasePath);
        VertexId first;
        using (var seed = database.BeginWriteTransaction())
        {
            CreateUniqueIndex(seed);
            first = seed.CreateVertex("Person");
            seed.SetProperty(first, "code", PropertyValue.FromString("shared"));
            seed.Commit();
        }

        VertexId second;
        using (var move = database.BeginWriteTransaction())
        {
            move.RemoveProperty(first, "code");
            second = move.CreateVertex("Person");
            move.SetProperty(second, "code", PropertyValue.FromString("shared"));
            move.Commit();
        }

        using (var reuse = database.BeginWriteTransaction())
        {
            reuse.DeleteVertex(second);
            VertexId third = reuse.CreateVertex("Person");
            reuse.SetProperty(third, "code", PropertyValue.FromString("shared"));
            reuse.Commit();
        }
    }

    [Fact]
    public void Savepoint_and_abort_restore_unique_visibility()
    {
        using var database = YatagarasuDatabase.Open(DatabasePath);
        VertexId first;
        using (var seed = database.BeginWriteTransaction())
        {
            CreateUniqueIndex(seed);
            first = seed.CreateVertex("Person");
            seed.SetProperty(first, "code", PropertyValue.FromString("kept"));
            seed.Commit();
        }

        using (var savepoint = database.BeginWriteTransaction())
        {
            SavepointId before = savepoint.Savepoint("before-release");
            savepoint.RemoveProperty(first, "code");
            VertexId provisional = savepoint.CreateVertex("Person");
            savepoint.SetProperty(provisional, "code", PropertyValue.FromString("kept"));
            savepoint.RollbackTo(before);

            VertexId conflict = savepoint.CreateVertex("Person");
            Action duplicate = () => savepoint.SetProperty(
                conflict,
                "code",
                PropertyValue.FromString("kept"));
            duplicate.Should().Throw<UniqueConstraintViolationException>();
            savepoint.Rollback();
        }

        using (var aborted = database.BeginWriteTransaction())
        {
            aborted.SetProperty(first, "code", PropertyValue.FromString("discarded"));
            aborted.Rollback();
        }

        using var write = database.BeginWriteTransaction();
        VertexId second = write.CreateVertex("Person");
        write.SetProperty(second, "code", PropertyValue.FromString("discarded"));
        write.Commit();
    }

    [Fact]
    public void Backfill_rejects_duplicates_without_publishing_definition()
    {
        using var database = YatagarasuDatabase.Open(DatabasePath);
        using (var seed = database.BeginWriteTransaction())
        {
            for (int i = 0; i < 2; i++)
            {
                VertexId vertex = seed.CreateVertex("Person");
                seed.SetProperty(vertex, "code", PropertyValue.FromString("duplicate"));
            }
            seed.Commit();
        }

        using (var schema = database.BeginWriteTransaction())
        {
            Action create = () => CreateUniqueIndex(schema);
            create.Should().Throw<UniqueConstraintViolationException>();
            schema.EditSchema.IndexExists(IndexName).Should().BeFalse();
            schema.Commit();
        }

        database.Schema.IndexExists(IndexName).Should().BeFalse();
    }

    [Fact]
    public void Unique_definition_persists_and_rebuild_fallback_enforces_it()
    {
        VertexId first;
        using (var database = YatagarasuDatabase.Open(DatabasePath))
        using (var seed = database.BeginWriteTransaction())
        {
            CreateUniqueIndex(seed);
            first = seed.CreateVertex("Person");
            seed.SetProperty(first, "code", PropertyValue.FromString("persisted"));
            seed.Commit();
        }

        using var reopened = YatagarasuDatabase.Open(DatabasePath);
        ScalarIndexDefinition definition = reopened.Schema.ListIndexes()
            .Single(info => info.Name == IndexName)
            .Definition.Should().BeOfType<ScalarIndexDefinition>().Subject;
        definition.Unique.Should().BeTrue();

        using var write = reopened.BeginWriteTransaction();
        var transactional = (TxIndexManager)write.AsInternal().Inner.Indexes;
        transactional.SetIndexState(IndexName, IndexLifecycleState.RebuildRequired);
        VertexId second = write.CreateVertex("Person");
        Action duplicate = () => write.SetProperty(
            second,
            "code",
            PropertyValue.FromString("persisted"));
        duplicate.Should().Throw<UniqueConstraintViolationException>();
        write.Rollback();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bulk_load_rejects_duplicate_unique_values_before_writing(bool streaming)
    {
        using var database = YatagarasuDatabase.Open(DatabasePath);
        LabelId label;
        PropertyKeyId key;
        using (var schema = database.BeginWriteTransaction())
        {
            label = schema.EditSchema.GetOrCreateLabel("Person");
            key = schema.EditSchema.GetOrCreatePropertyKey("code");
            CreateUniqueIndex(schema);
            schema.Commit();
        }

        Action commit;
        if (streaming)
        {
            var loader = database.BeginStreamingBulkLoad();
            AppendDuplicates(loader, label, key);
            commit = () =>
            {
                using (loader)
                    loader.Commit();
            };
        }
        else
        {
            var loader = database.BeginBulkLoad();
            AppendDuplicates(loader, label, key);
            commit = () =>
            {
                using (loader)
                    loader.Commit();
            };
        }

        commit.Should().Throw<UniqueConstraintViolationException>();
        using var read = database.BeginReadTransaction();
        read.AsInternal().Inner.Vertices.Scan().Should().BeEmpty();
    }

    [Fact]
    public void Unique_option_rejects_unsupported_targets_and_cardinality()
    {
        using var database = YatagarasuDatabase.Open(DatabasePath);
        using var write = database.BeginWriteTransaction();
        write.EditSchema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        Action edge = () => write.EditSchema.CreateIndex(new ScalarIndexDefinition(
            "idx_edge",
            new PropertyTarget(PropertyOwnerKind.Edge, "code", "LINKS"),
            IndexKind.StringEquality,
            Unique: true));
        Action range = () => write.EditSchema.CreateIndex(new ScalarIndexDefinition(
            "idx_range",
            new PropertyTarget(PropertyOwnerKind.Vertex, "code", "Person"),
            IndexKind.StringRange,
            Unique: true));
        Action set = () => write.EditSchema.CreateIndex(new ScalarIndexDefinition(
            "idx_set",
            new PropertyTarget(PropertyOwnerKind.Vertex, "tags", "Person"),
            IndexKind.StringEquality,
            Unique: true));

        edge.Should().Throw<ConstraintException>();
        range.Should().Throw<ConstraintException>();
        set.Should().Throw<ConstraintException>();
        write.Rollback();
    }

    private static void CreateUniqueIndex(IWriteTransaction transaction)
        => transaction.EditSchema.CreateIndex(new ScalarIndexDefinition(
            IndexName,
            new PropertyTarget(PropertyOwnerKind.Vertex, "code", "Person"),
            IndexKind.StringEquality,
            Unique: true));

    private static void AppendDuplicates(
        BulkLoader loader,
        LabelId label,
        PropertyKeyId key)
    {
        for (int i = 0; i < 2; i++)
        {
            VertexId vertex = VertexId.Create(i, 1);
            loader.AppendVertex(vertex, label);
            loader.AppendProperty(vertex, key, PropertyValue.FromString("duplicate"));
        }
    }

    private static void AppendDuplicates(
        StreamingBulkLoader loader,
        LabelId label,
        PropertyKeyId key)
    {
        for (int i = 0; i < 2; i++)
        {
            VertexId vertex = VertexId.Create(i, 1);
            loader.AppendVertex(vertex, label);
            loader.AppendProperty(vertex, key, PropertyValue.FromString("duplicate"));
        }
    }
}

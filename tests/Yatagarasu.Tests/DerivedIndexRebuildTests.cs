using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Index;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Tests;

[Collection("binary-backend-maintenance")]
public sealed class DerivedIndexRebuildTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "yatagarasu_derived_rebuild_" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "graph.yata");

    [Fact]
    public void Missing_scalar_artifact_falls_back_then_rebuilds_from_primary_property()
    {
        VertexId vertex;
        using (var database = YatagarasuDatabase.Open(DatabasePath))
        {
            using (var write = database.BeginWriteTransaction())
            {
                write.EditSchema.CreateIndex(new ScalarIndexDefinition(
                    "idx_external_id",
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        "externalId",
                        "Document"),
                    IndexKind.StringEquality));
                vertex = write.CreateVertex("Document");
                write.SetProperty(
                    vertex,
                    "externalId",
                    PropertyValue.FromString("doc-1"));
                write.Commit();
            }

            using var damage = database.BeginWriteTransaction();
            var transactional = (TxIndexManager)damage.AsInternal().Inner.Indexes;
            ((IndexManager)transactional.Inner)
                .TruncateIndexArtifactForTest("idx_external_id");
            damage.Commit();
        }

        using var reopened = YatagarasuDatabase.Open(DatabasePath);
        using (var fallback = reopened.BeginReadTransaction())
        {
            fallback.Schema.ListIndexes()
                .Single(index => index.Name == "idx_external_id")
                .State.Should().Be(IndexLifecycleState.RebuildRequired);
            Seek(fallback, "idx_external_id", "doc-1")
                .Should().Equal(EntityRef.From(vertex));
        }

        using (var publish = reopened.BeginWriteTransaction())
            publish.Commit();

        bool rebuilt = SpinWait.SpinUntil(
            () => reopened.Schema.ListIndexes()
                .Single(index => index.Name == "idx_external_id")
                .State == IndexLifecycleState.Ready,
            TimeSpan.FromSeconds(5));
        var backend = (BinaryGraphStorageBackend)reopened.BackendInternal;
        rebuilt.Should().BeTrue(
            "the scalar artifact should be rebuilt from primary state; error: {0}",
            backend.ScalarIndexRebuildErrorForTest);
    }

    [Fact]
    public void Missing_full_text_segment_rebuilds_search_from_primary_property()
    {
        VertexId document;
        using (var database = YatagarasuDatabase.Open(DatabasePath))
        {
            database.EditSchema(schema => schema.CreateIndex(
                new FullTextIndexDefinition(
                    "idx_body",
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        "body",
                        "Document"),
                    SegmentPolicy: new FullTextSegmentPolicy(
                        MaximumDeltaEntries: 1,
                        MaximumSegments: 2))));
            using var write = database.BeginWriteTransaction();
            document = write.CreateVertex("Document");
            write.SetProperty(
                document,
                "body",
                PropertyValue.FromString("kept marker"));
            write.Commit();
            var backend = (BinaryGraphStorageBackend)database.BackendInternal;
            backend.WaitForFullTextSegmentMergeForTest();
            backend.FullTextSegmentMergeErrorForTest.Should().BeNull();
        }

        string artifactDirectory = DatabasePath + "-ftseg";
        Directory.EnumerateFiles(artifactDirectory, "*.qfts")
            .Should().NotBeEmpty();
        foreach (string artifact in Directory.EnumerateFiles(
                     artifactDirectory,
                     "*.qfts"))
            File.Delete(artifact);

        using var reopened = YatagarasuDatabase.Open(DatabasePath);
        using var read = reopened.BeginReadTransaction();
        read.Query.Search("idx_body", "marker", 10).ToList()
            .Should().ContainSingle().Which.Should().Be(document);
    }

    [Fact]
    public void Missing_vector_derived_state_rebuilds_search_from_primary_property()
    {
        VertexId document;
        using (var database = YatagarasuDatabase.Open(DatabasePath))
        {
            using var write = database.BeginWriteTransaction();
            write.EditSchema.CreateIndex(new VectorIndexDefinition(
                "idx_embedding",
                new PropertyTarget(
                    PropertyOwnerKind.Vertex,
                    "embedding",
                    "Document"),
                2,
                SegmentPolicy: new VectorSegmentPolicy(
                    MaximumDeltaEntries: 1,
                    MaximumSegments: 2)));
            document = write.CreateVertex("Document");
            write.SetVectorProperty(
                EntityRef.From(document),
                "embedding",
                [1f, 0f]);
            write.Commit();
            var backend = (BinaryGraphStorageBackend)database.BackendInternal;
            backend.WaitForVectorSegmentMergeForTest();
            backend.VectorSegmentMergeErrorForTest.Should().BeNull();
        }

        using var reopened = YatagarasuDatabase.Open(DatabasePath);
        using var read = reopened.BeginReadTransaction();
        using VectorSearchCursor hits = read.KnnSearch(
            "idx_embedding",
            [1f, 0f],
            10);
        hits.MoveNext().Should().BeTrue();
        hits.Current.Owner.Should().Be(EntityRef.From(document));
        hits.MoveNext().Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static List<EntityRef> Seek(
        IReadTransaction transaction,
        string indexName,
        string value)
    {
        using EntityRefEnumerator hits = transaction.SeekIndex(
            indexName,
            PropertyValue.FromString(value));
        var result = new List<EntityRef>();
        while (hits.MoveNext())
            result.Add(hits.Current);
        return result;
    }
}

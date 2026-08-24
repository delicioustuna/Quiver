using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Index.Vector;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// immutable vector segmentのmanifest可視性とstale artifact拒否を検証する。
/// </summary>
public sealed class VectorSegmentSnapshotTests
{
    private static readonly VectorIndexDefinition Definition = new(
        "embedding_idx",
        new PropertyTarget(PropertyOwnerKind.Vertex, "embedding", "Doc"),
        2,
        SegmentPolicy: new VectorSegmentPolicy(
            MaximumDeltaEntries: 1,
            MaximumSegments: 2));

    [Fact]
    public void Publish_switches_old_and_new_manifest_by_snapshot()
    {
        using var index = new VectorSegmentIndex();
        EntityRef first = EntityRef.Create(EntityKind.Vertex, 1, 3);
        var mutation = VectorSegmentMutation.Upsert(Definition, first, [1f, 0f]);

        index.PublishDelta(2, [mutation]);

        VectorSegmentSearchResult before = index.Search(
            new SnapshotState(1, new HashSet<long>()),
            Definition,
            [1f, 0f],
            1,
            null);
        VectorSegmentSearchResult after = index.Search(
            new SnapshotState(2, new HashSet<long>()),
            Definition,
            [1f, 0f],
            1,
            null);

        before.Candidates.Should().BeEmpty();
        after.Candidates.Should().ContainSingle()
            .Which.Owner.Should().Be(first);

        VectorSegmentBuildSource source = index.CaptureBuildSources().Single();
        VectorSegmentBuildArtifact artifact = VectorSegmentIndex.Build(source, [mutation]);
        index.TryPublishMerge(3, artifact, Definition).Should().BeTrue();

        VectorSegmentSearchResult oldReader = index.Search(
            new SnapshotState(2, new HashSet<long>()),
            Definition,
            [1f, 0f],
            1,
            null);
        VectorSegmentSearchResult newReader = index.Search(
            new SnapshotState(3, new HashSet<long>()),
            Definition,
            [1f, 0f],
            1,
            null);

        oldReader.ManifestGeneration.Should().Be(source.ManifestGeneration);
        oldReader.CoversPrimarySnapshot.Should().BeFalse();
        newReader.ManifestGeneration.Should().Be(source.ManifestGeneration + 1);
        newReader.CoversPrimarySnapshot.Should().BeTrue();
        newReader.Candidates.Should().ContainSingle()
            .Which.Owner.Should().Be(first);
    }

    [Fact]
    public void Publish_rejects_artifact_when_source_generation_changed()
    {
        using var index = new VectorSegmentIndex();
        EntityRef first = EntityRef.Create(EntityKind.Vertex, 1, 0);
        EntityRef second = EntityRef.Create(EntityKind.Vertex, 2, 0);
        VectorSegmentMutation firstMutation =
            VectorSegmentMutation.Upsert(Definition, first, [1f, 0f]);
        index.PublishDelta(2, [firstMutation]);

        VectorSegmentBuildSource source = index.CaptureBuildSources().Single();
        VectorSegmentBuildArtifact stale = VectorSegmentIndex.Build(
            source,
            [firstMutation]);
        index.PublishDelta(
            3,
            [VectorSegmentMutation.Upsert(Definition, second, [0f, 1f])]);

        index.TryPublishMerge(4, stale, Definition).Should().BeFalse();
        stale.Segment.Dispose();

        VectorSegmentSearchResult current = index.Search(
            new SnapshotState(3, new HashSet<long>()),
            Definition,
            [0f, 1f],
            2,
            null);
        current.Candidates.Select(static candidate => candidate.Owner)
            .Should().Contain([first, second]);
    }

    [Fact]
    public void Garbage_collection_retires_only_manifests_older_than_horizon()
    {
        using var index = new VectorSegmentIndex();
        EntityRef owner = EntityRef.Create(EntityKind.Vertex, 1, 1);
        var mutation = VectorSegmentMutation.Upsert(Definition, owner, [1f, 0f]);
        index.PublishDelta(2, [mutation]);
        VectorSegmentBuildSource source = index.CaptureBuildSources().Single();
        VectorSegmentBuildArtifact artifact = VectorSegmentIndex.Build(source, [mutation]);
        index.TryPublishMerge(3, artifact, Definition).Should().BeTrue();

        index.CollectGarbage(3, dryRun: false).Should().Be(1);
        index.Search(
                new SnapshotState(2, new HashSet<long>()),
                Definition,
                [1f, 0f],
                1,
                null)
            .ManifestGeneration.Should().Be(source.ManifestGeneration);

        index.CollectGarbage(4, dryRun: false).Should().Be(1);
        index.Search(
                new SnapshotState(2, new HashSet<long>()),
                Definition,
                [1f, 0f],
                1,
                null)
            .Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Background_merge_builds_outside_writer_and_preserves_old_reader()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "yatagarasu_vector_segment_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = YatagarasuDatabase.Open(Path.Combine(dir, "graph.yata"));
            using (var schema = database.BeginWriteTransaction())
            {
                schema.EditSchema.CreateIndex(Definition);
                schema.Commit();
            }

            VertexId first;
            VertexId second;
            using (var write = database.BeginWriteTransaction())
            {
                first = write.CreateVertex("Doc");
                second = write.CreateVertex("Doc");
                write.SetVectorProperty(EntityRef.From(first), "embedding", [1f, 0f]);
                write.SetVectorProperty(EntityRef.From(second), "embedding", [0f, 1f]);
                write.Commit();
            }

            var backend = (BinaryGraphStorageBackend)database.BackendInternal;
            backend.WaitForVectorSegmentMergeForTest();
            backend.VectorSegmentMergeErrorForTest.Should().BeNull();

            using var oldReader = database.BeginReadTransaction();
            using (var update = database.BeginWriteTransaction())
            {
                update.SetVectorProperty(EntityRef.From(first), "embedding", [-1f, 0f]);
                update.Commit();
            }

            using var oldCursor = oldReader.KnnSearch("embedding_idx", [1f, 0f], 1);
            oldCursor.MoveNext().Should().BeTrue();
            oldCursor.Current.Owner.Should().Be(EntityRef.From(first));

            using var newReader = database.BeginReadTransaction();
            using var newCursor = newReader.KnnSearch("embedding_idx", [1f, 0f], 1);
            newCursor.MoveNext().Should().BeTrue();
            newCursor.Current.Owner.Should().Be(EntityRef.From(second));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

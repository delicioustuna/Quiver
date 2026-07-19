using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

[Collection("full-text-segment-worker")]
public sealed class FullTextDurableArtifactTests : IDisposable
{
    private const string Index = "body_idx";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "quiver_ft_artifact_" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "graph.quiver");

    [Fact]
    public void Normal_reopen_loads_durable_segment_without_primary_scan()
    {
        VertexId document = CreateMergedDatabase("durable marker");

        using var reopened = QuiverDatabase.Open(DatabasePath);
        Search(reopened, "marker").Should().ContainSingle().Which.Should().Be(document);
        ((BinaryGraphStorageBackend)reopened.BackendInternal)
            .FullTextPrimaryFallbackScanCountForTest.Should().Be(0,
            "a valid manifest must reopen immutable segment bodies directly");
    }

    [Fact]
    public void Corrupt_referenced_body_falls_back_to_primary_rebuild()
    {
        VertexId document = CreateMergedDatabase("recover marker");
        string artifactPath = DatabasePath + "-ftseg";
        using (var stream = new FileStream(
                   artifactPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            stream.Position = stream.Length - 1;
            int value = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(value ^ 0x5A));
            stream.Flush(flushToDisk: true);
        }

        using var buildStarted = new ManualResetEventSlim();
        using var releaseBuild = new ManualResetEventSlim();
        BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = () =>
        {
            buildStarted.Set();
            releaseBuild.Wait(TimeSpan.FromSeconds(10));
        };
        try
        {
            using var reopened = QuiverDatabase.Open(DatabasePath);
            Search(reopened, "marker").Should().ContainSingle().Which.Should().Be(document);
            ((BinaryGraphStorageBackend)reopened.BackendInternal)
                .FullTextPrimaryFallbackScanCountForTest.Should().BeGreaterThan(0,
                "a checksum failure must not make the derived index authoritative");
            buildStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            reopened.Schema.ListIndexes().Single(x => x.Name == Index)
                .State.Should().Be(IndexLifecycleState.RebuildRequired);
            releaseBuild.Set();
            ((BinaryGraphStorageBackend)reopened.BackendInternal)
                .WaitForFullTextSegmentMergeForTest();
            reopened.Schema.ListIndexes().Single(x => x.Name == Index)
                .State.Should().Be(IndexLifecycleState.Ready);
        }
        finally
        {
            releaseBuild.Set();
            BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = null;
        }
    }

    [Theory]
    [InlineData((int)FullTextArtifactPhase.AfterBodyFsync)]
    [InlineData((int)FullTextArtifactPhase.AfterManifestStaged)]
    public void Aborted_publish_boundary_keeps_new_body_invisible(
        int failurePhaseValue)
    {
        var failurePhase = (FullTextArtifactPhase)failurePhaseValue;
        VertexId document = CreateMergedDatabase("oldterm");
        using (var database = QuiverDatabase.Open(DatabasePath))
        {
            FullTextSegmentIndex.ArtifactPhaseInjector = phase =>
            {
                if (phase == failurePhase)
                    throw new InjectedFailureException();
            };
            using var update = database.BeginWriteTransaction();
            update.SetProperty(
                document,
                "body",
                PropertyValue.FromString("newterm"));
            update.Invoking(static transaction => transaction.Commit())
                .Should().Throw<InjectedFailureException>();
        }

        FullTextSegmentIndex.ArtifactPhaseInjector = null;
        BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = null;
        using var reopened = QuiverDatabase.Open(DatabasePath);
        Search(reopened, "oldterm").Should().ContainSingle().Which.Should().Be(document);
        Search(reopened, "newterm").Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_copies_referenced_fulltext_artifacts()
    {
        VertexId document = CreateMergedDatabase("snapshot marker");
        string snapshotPath = Path.Combine(_directory, "copy", "graph.quiver");
        using (var source = QuiverDatabase.Open(DatabasePath))
            source.CreateSnapshot(snapshotPath);

        File.Exists(snapshotPath + "-ftseg").Should().BeTrue();
        using var snapshot = QuiverDatabase.Open(snapshotPath);
        Search(snapshot, "marker").Should().ContainSingle().Which.Should().Be(document);
        ((BinaryGraphStorageBackend)snapshot.BackendInternal)
            .FullTextPrimaryFallbackScanCountForTest.Should().Be(0);
    }

    private VertexId CreateMergedDatabase(string body)
    {
        Directory.CreateDirectory(_directory);
        using var database = QuiverDatabase.Open(DatabasePath);
        database.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(
            Index,
            new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"),
            SegmentPolicy: new FullTextSegmentPolicy(
                MaximumDeltaEntries: 1,
                MaximumSegments: 2))));
        VertexId document;
        using (var transaction = database.BeginWriteTransaction())
        {
            document = transaction.CreateVertex("Doc");
            transaction.SetProperty(
                document,
                "body",
                PropertyValue.FromString(body));
            transaction.Commit();
        }
        var backend = (BinaryGraphStorageBackend)database.BackendInternal;
        backend.WaitForFullTextSegmentMergeForTest();
        backend.FullTextSegmentMergeErrorForTest.Should().BeNull();
        File.Exists(DatabasePath + "-ftseg").Should().BeTrue();
        return document;
    }

    private static List<VertexId> Search(QuiverDatabase database, string query)
    {
        using var transaction = database.BeginReadTransaction();
        return transaction.Query.Search(Index, query, 10).ToList();
    }

    public void Dispose()
    {
        FullTextSegmentIndex.ArtifactPhaseInjector = null;
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class InjectedFailureException : Exception;
}

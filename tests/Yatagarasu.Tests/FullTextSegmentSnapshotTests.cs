using System.Collections.Concurrent;
using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

[CollectionDefinition("full-text-segment-worker", DisableParallelization = true)]
public sealed class FullTextSegmentWorkerCollection;

/// <summary>全文segment workerのlease境界とstale artifact拒否を検証する。</summary>
[Collection("full-text-segment-worker")]
public sealed class FullTextSegmentSnapshotTests
{
    private static readonly FullTextIndexDefinition Definition = new(
        "body_idx",
        new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"),
        SegmentPolicy: new FullTextSegmentPolicy(
            MaximumDeltaEntries: 1,
            MaximumSegments: 2));

    [Fact]
    public async Task Writer_progress_invalidates_stale_build_and_merge_retries()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "yatagarasu_fulltext_segment_" + Guid.NewGuid().ToString("N"));
        using var buildStarted = new ManualResetEventSlim();
        using var releaseBuild = new ManualResetEventSlim();
        int buildCount = 0;
        var publishDurations = new ConcurrentQueue<TimeSpan>();
        BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = () =>
        {
            Interlocked.Increment(ref buildCount);
            buildStarted.Set();
            releaseBuild.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        };
        BinaryGraphStorageBackend.FullTextSegmentPublishMeasuredForTest =
            publishDurations.Enqueue;

        try
        {
            using var database = YatagarasuDatabase.Open(Path.Combine(dir, "graph.yata"));
            database.EditSchema(schema => schema.CreateIndex(Definition));

            VertexId document;
            using (var seed = database.BeginWriteTransaction())
            {
                document = seed.CreateVertex("Doc");
                seed.SetProperty(
                    document,
                    "body",
                    PropertyValue.FromString("before merge"));
                seed.Commit();
            }

            buildStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            Task writer = Task.Run(() =>
            {
                using var update = database.BeginWriteTransaction();
                update.SetProperty(
                    document,
                    "body",
                    PropertyValue.FromString("after merge"));
                update.Commit();
            });
            await writer.WaitAsync(TimeSpan.FromSeconds(2));

            releaseBuild.Set();
            var backend = (BinaryGraphStorageBackend)database.BackendInternal;
            backend.WaitForFullTextSegmentMergeForTest();
            backend.FullTextSegmentMergeErrorForTest.Should().BeNull();
            Volatile.Read(ref buildCount).Should().BeGreaterThanOrEqualTo(2,
                "the generation change must reject and retry the first artifact");

            using var read = database.BeginReadTransaction();
            read.Query.Search("body_idx", "before", 10).ToList().Should().BeEmpty();
            read.Query.Search("body_idx", "after", 10).ToList()
                .Should().ContainSingle().Which.Should().Be(document);
            publishDurations.Should().NotBeEmpty();
            publishDurations.Max().Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            releaseBuild.Set();
            BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = null;
            BinaryGraphStorageBackend.FullTextSegmentPublishMeasuredForTest = null;
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

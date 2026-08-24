using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Index.FullText;
using Yatagarasu.Storage.Records;
using Yatagarasu.Telemetry;
using Xunit;

namespace Yatagarasu.Tests;

[CollectionDefinition("runtime-diagnostics", DisableParallelization = true)]
public sealed class RuntimeDiagnosticsCollection;

[Collection("runtime-diagnostics")]
public sealed class RuntimeDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "yatagarasu_runtime_diagnostics_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Snapshot_diagnostics_and_observable_metrics_report_the_oldest_reader()
    {
        using var measurements = new MeasurementCapture();
        using var database = YatagarasuDatabase.Open(Path.Combine(_directory, "graph.yata"));
        using var reader = database.BeginReadTransaction();

        SnapshotRuntimeDiagnostics diagnostics =
            database.Diagnostics.GetSnapshotDiagnostics();
        measurements.RecordObservableInstruments();

        diagnostics.ActiveCount.Should().Be(1);
        diagnostics.OldestAge.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        diagnostics.OldestStartLocation.Should().Be("BeginRead");
        diagnostics.OldestCommittedHighWater.Should().NotBeNull();
        measurements.Values("yatagarasu.snapshot.active.count")
            .Should().Contain(value => value >= 1);
        measurements.Values("yatagarasu.snapshot.oldest.age")
            .Should().Contain(value => value >= 0);
    }

    [Fact]
    public async Task Writer_contention_records_count_and_actual_wait_duration()
    {
        using var measurements = new MeasurementCapture();
        using var database = YatagarasuDatabase.Open(
            Path.Combine(_directory, "writer.yata"),
            new YatagarasuDatabaseOptions
            {
                WriterWaitTimeout = TimeSpan.FromSeconds(2),
            });
        using var first = database.BeginWriteTransaction();
        Task<IWriteTransaction> waiting =
            Task.Run(() => database.BeginWriteTransaction());
        await Task.Delay(50);

        first.Rollback();
        using IWriteTransaction second = await waiting;
        second.Rollback();

        measurements.Values("yatagarasu.writer.contention.count")
            .Should().Contain(value => value >= 1);
        measurements.Values("yatagarasu.writer.wait.duration")
            .Should().Contain(value => value > 0);
    }

    [Fact]
    public void Maintenance_metrics_balance_rebuild_and_garbage_collection_state()
    {
        using var measurements = new MeasurementCapture();
        using var rebuildStarted = new ManualResetEventSlim();
        using var releaseRebuild = new ManualResetEventSlim();
        BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = () =>
        {
            rebuildStarted.Set();
            releaseRebuild.Wait(TimeSpan.FromSeconds(10));
        };
        using var database = YatagarasuDatabase.Open(
            Path.Combine(_directory, "maintenance.yata"));
        try
        {
            database.EditSchema(schema => schema.CreateIndex(
                new FullTextIndexDefinition(
                    "body_idx",
                    new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"),
                    SegmentPolicy: new FullTextSegmentPolicy(
                        MaximumDeltaEntries: 1,
                        MaximumSegments: 2))));
            using (var write = database.BeginWriteTransaction())
            {
                VertexId document = write.CreateVertex("Doc");
                write.SetProperty(
                    document,
                    "body",
                    PropertyValue.FromString("maintenance state"));
                write.Commit();
            }

            rebuildStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            measurements.Values("yatagarasu.maintenance.rebuild.active")
                .Should().Contain(1);
            releaseRebuild.Set();
            ((BinaryGraphStorageBackend)database.BackendInternal)
                .WaitForFullTextSegmentMergeForTest();
            database.Vacuum();

            measurements.Values("yatagarasu.maintenance.rebuild.active")
                .Should().ContainInOrder(1, -1);
            measurements.Values("yatagarasu.maintenance.gc.active")
                .Should().ContainInOrder(1, -1);
        }
        finally
        {
            releaseRebuild.Set();
            BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = null;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class MeasurementCapture : IDisposable
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<double>>
            _measurements = new(StringComparer.Ordinal);
        private readonly MeterListener _listener = new();

        internal MeasurementCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == YatagarasuTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, _, _) => Add(instrument.Name, measurement));
            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, _, _) => Add(instrument.Name, measurement));
            _listener.Start();
        }

        internal IEnumerable<double> Values(string instrumentName)
            => _measurements.TryGetValue(
                instrumentName,
                out ConcurrentQueue<double>? values)
                ? values
                : [];

        internal void RecordObservableInstruments()
            => _listener.RecordObservableInstruments();

        private void Add(string instrumentName, double measurement)
            => _measurements.GetOrAdd(
                instrumentName,
                static _ => new ConcurrentQueue<double>()).Enqueue(measurement);

        public void Dispose() => _listener.Dispose();
    }
}

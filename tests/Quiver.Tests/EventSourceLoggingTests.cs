using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using FluentAssertions;
using Quiver.Query.Physical;
using Quiver.Telemetry;
using Xunit;

namespace Quiver.Tests;

/// <summary>core が追加依存なしで公開する構造化 EventSource 契約を検証する。</summary>
public sealed class EventSourceLoggingTests : IDisposable
{
    private readonly string _dir;
    private readonly CapturingEventListener _listener;
    private readonly QuiverDatabase _db;

    public EventSourceLoggingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_eventsource_" + Guid.NewGuid().ToString("N"));
        _listener = new CapturingEventListener();
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _listener.Dispose();
        if (Directory.Exists(_dir))
            try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Commit_emits_information_event_with_tx_id()
    {
        long txId;
        using (var tx = _db.BeginWriteTransaction())
        {
            txId = tx.Id.Value;
            tx.CreateVertex("Person");
            tx.Commit();
        }

        var entry = _listener.Events
            .First(e => e.EventId == 1 && e.Int64At(0) == txId);
        entry.Level.Should().Be(EventLevel.Informational);
        entry.DoubleAt(1).Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Rollback_emits_warning_event_with_tx_id()
    {
        long txId;
        using (var tx = _db.BeginWriteTransaction())
        {
            txId = tx.Id.Value;
            tx.CreateVertex("Person");
            tx.Rollback();
        }

        _listener.Events.Should()
            .Contain(e => e.EventId == 2 && e.Int64At(0) == txId);
        _listener.Events
            .First(e => e.EventId == 2 && e.Int64At(0) == txId)
            .Level.Should().Be(EventLevel.Warning);
    }

    [Fact]
    public void Query_emits_verbose_event_with_tx_id_and_row_count()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            tx.CreateVertex("Person");
            tx.CreateVertex("Person");
            tx.CreateVertex("Person");
            tx.Commit();
        }

        long txId;
        using (var tx = _db.BeginReadTransaction())
        {
            txId = tx.Id.Value;
            _db.Schema.TryGetLabelId("Person", out var labelId).Should().BeTrue();
            tx.Execute(new VertexByLabelScanOperator(labelId));
        }

        var entry = _listener.Events
            .First(e => e.EventId == 30 && e.Int64At(0) == txId && e.Int32At(1) == 3);
        entry.Level.Should().Be(EventLevel.Verbose);
    }

    [Fact]
    public void Old_snapshot_warning_preserves_start_location_and_high_water()
    {
        QuiverEventSource.Log.OldSnapshotDetected(
            activeCount: 2,
            oldestAgeSeconds: 301,
            startLocation: "BeginRead",
            committedHighWater: 42);

        CapturedEvent entry = _listener.Events.First(e => e.EventId == 40);
        entry.Level.Should().Be(EventLevel.Warning);
        entry.Int32At(0).Should().Be(2);
        entry.DoubleAt(1).Should().Be(301);
        entry.StringAt(2).Should().Be("BeginRead");
        entry.Int64At(3).Should().Be(42);
    }

    private sealed class CapturingEventListener : EventListener
    {
        public ConcurrentBag<CapturedEvent> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == QuiverEventSource.EventSourceName)
                EnableEvents(eventSource, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId is not (1 or 2 or 3 or 10 or 20 or 30 or 40)) return;
            Events.Add(new CapturedEvent(
                eventData.EventId,
                eventData.Level,
                eventData.Payload?.ToArray() ?? []));
        }
    }

    private sealed record CapturedEvent(int EventId, EventLevel Level, object?[] Payload)
    {
        public long Int64At(int index) => Convert.ToInt64(Payload[index]);
        public int Int32At(int index) => Convert.ToInt32(Payload[index]);
        public double DoubleAt(int index) => Convert.ToDouble(Payload[index]);
        public string StringAt(int index) => Convert.ToString(Payload[index])!;
    }
}

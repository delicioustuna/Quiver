using FluentAssertions;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Transactions.Tests;

public sealed class SnapshotRegistryTests
{
    [Fact]
    public void Horizon_does_not_advance_past_fallback_when_pending_entry_binds_during_scan()
    {
        var registry = new SnapshotRegistry();
        using var pending = registry.Reserve();
        var newer = new SnapshotState(20, SnapshotState.Empty.AbortedGaps);
        registry.HorizonCapturedForTest = () => pending.Bind(in newer);
        registry.OldestCommittedHighWater(10).Should().Be(10);
    }

    [Fact]
    public void Horizon_capture_remains_conservative_when_reader_finishes_during_scan()
    {
        var registry = new SnapshotRegistry();
        var snapshot = new SnapshotState(5, SnapshotState.Empty.AbortedGaps);
        var reader = registry.Register(in snapshot);
        registry.HorizonCapturedForTest = reader.Dispose;
        registry.OldestCommittedHighWater(10).Should().Be(5);
        registry.HorizonCapturedForTest = null;
        registry.OldestCommittedHighWater(10).Should().Be(10);
    }

    [Fact]
    public void Recovery_and_prune_publish_whole_immutable_snapshots()
    {
        var registry = new CommittedTxRegistry();
        registry.RestoreCheckpointedHighWater(10);
        var checkpoint = registry.Published;
        int publications = 0;
        registry.BeforePublishForTest = () =>
        {
            publications++;
            registry.Published.Should().BeSameAs(checkpoint);
        };
        registry.RestoreRecoveredTransactions(new long[] { 12, 14 }, new long[] { 11, 12, 13, 14 }, 14);
        registry.BeforePublishForTest = null;
        publications.Should().Be(1);
        var recovered = registry.Published;
        recovered.Snapshot.CommittedHighWater.Should().Be(14);
        recovered.Snapshot.AbortedGaps.Should().BeEquivalentTo(new long[] { 11, 13 });
        recovered.Snapshot.AbortedGaps.Should().BeAssignableTo<System.Collections.Frozen.FrozenSet<long>>();
        registry.CompactedVisibilityHorizon = 14;
        registry.PruneBelow(15);
        registry.Published.Snapshot.AbortedGaps.Should().BeEmpty();
        recovered.Snapshot.AbortedGaps.Should().BeEquivalentTo(new long[] { 11, 13 });
        checkpoint.Snapshot.CommittedHighWater.Should().Be(10);
    }

    [Fact]
    public void Registrations_within_warning_window_report_only_once()
    {
        int warnings = 0;
        var registry = new SnapshotRegistry(TimeSpan.Zero, _ => Interlocked.Increment(ref warnings), new ManualTimeProvider());
        var snapshot = SnapshotState.Empty;
        using var held = registry.Register(in snapshot);
        for (int i = 0; i < 100; i++)
        {
            using var registration = registry.Register(in snapshot);
        }
        warnings.Should().Be(1);
    }

    [Fact]
    public void Empty_capture_reuses_immutable_empty_gaps()
    {
        var registry = new CommittedTxRegistry();
        SnapshotState first = registry.Published.Snapshot;
        first.AbortedGaps.Should().BeSameAs(SnapshotState.Empty.AbortedGaps);
        first.AbortedGaps.Should().BeAssignableTo<System.Collections.Frozen.FrozenSet<long>>();
        for (int i = 0; i < 100; i++) _ = registry.Published;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) _ = registry.Published;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().Be(0);

        registry.MarkAborted(new TransactionId(2));
        registry.MarkCommitted(new TransactionId(3));
        registry.SetActiveWriter(new TransactionId(4));
        SnapshotState withGap = registry.Published.Snapshot;
        withGap.AbortedGaps.Should().Equal(2L);
        withGap.ActiveWriterId.Should().Be(new TransactionId(4));
        registry.MarkAborted(new TransactionId(4));
        registry.MarkCommitted(new TransactionId(5));
        registry.Published.Snapshot.AbortedGaps.Should().BeEquivalentTo(new long[] { 2, 4 });
        registry.PruneBelow(6);
        withGap.AbortedGaps.Should().Equal(2L);
        first.AbortedGaps.Should().BeEmpty();
        registry.Published.Snapshot.AbortedGaps.Should().BeSameAs(first.AbortedGaps);
    }

    [Fact]
    public void Concurrent_registrations_share_check_window_and_warn_after_threshold()
    {
        var clock = new ManualTimeProvider();
        int warnings = 0;
        var registry = new SnapshotRegistry(TimeSpan.FromSeconds(1), _ => Interlocked.Increment(ref warnings), clock);
        var snapshot = SnapshotState.Empty;
        using var held = registry.Register(in snapshot, "held");
        clock.Advance(TimeSpan.FromMilliseconds(999));
        using (registry.Register(in snapshot)) { }
        warnings.Should().Be(0);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using (registry.Register(in snapshot)) { }
        warnings.Should().Be(0);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Parallel.For(0, 100, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
        {
            using var registration = registry.Register(in snapshot);
        });
        warnings.Should().Be(1);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        using (registry.Register(in snapshot)) { }
        warnings.Should().Be(2);
        registry.Diagnostics.OldestStartLocation.Should().Be("held");
        registry.ActiveCount.Should().Be(1);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }

    [Fact]
    public void Registration_reports_count_age_location_and_high_water_then_releases_once()
    {
        var warnings = new List<SnapshotDiagnostics>();
        var registry = new SnapshotRegistry(TimeSpan.Zero, warnings.Add);
        var snapshot = new SnapshotState(42, new HashSet<long>());

        var registration = registry.Register(in snapshot, "reader-test");
        SnapshotDiagnostics diagnostics = registry.Diagnostics;
        diagnostics.ActiveCount.Should().Be(1);
        diagnostics.OldestAge.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        diagnostics.OldestStartLocation.Should().Be("reader-test");
        diagnostics.OldestCommittedHighWater.Should().Be(42);

        registration.Dispose();
        registration.Dispose();
        registry.Diagnostics.ActiveCount.Should().Be(0);
        warnings.Should().ContainSingle();
    }

    [Fact]
    public void Oldest_high_water_tracks_long_reader_without_forcing_expiry()
    {
        var registry = new SnapshotRegistry(TimeSpan.Zero);
        var oldSnapshot = new SnapshotState(5, new HashSet<long>());
        var newSnapshot = new SnapshotState(9, new HashSet<long>());
        using var oldRegistration = registry.Register(in oldSnapshot, "old");
        using var newRegistration = registry.Register(in newSnapshot, "new");

        registry.OldestCommittedHighWater(99).Should().Be(5);
        registry.ActiveCount.Should().Be(2);
    }
}

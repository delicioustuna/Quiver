using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Transactions.Tests;

public class VisibilityTests
{
    private static readonly TransactionId Self = new(10);

    private static SnapshotState Snapshot(
        long highWater = 20,
        long[]? aborted = null,
        long? activeWriter = null)
        => new(
            highWater,
            new HashSet<long>(aborted ?? []),
            activeWriter is null ? null : new TransactionId(activeWriter.Value));

    [Fact]
    public void Xmin_zero_is_invisible()
    {
        SnapshotState snapshot = Snapshot();
        Visibility.IsVisible(0, 0, in snapshot, Self).Should().BeFalse();
    }

    [Fact]
    public void Own_write_is_visible_and_own_delete_hides_it()
    {
        SnapshotState snapshot = Snapshot();
        Visibility.IsVisible(Self.Value, 0, in snapshot, Self).Should().BeTrue();
        Visibility.IsVisible(Self.Value, Self.Value, in snapshot, Self).Should().BeFalse();
    }

    [Fact]
    public void Xmin_at_or_below_high_water_is_visible_unless_aborted()
    {
        SnapshotState snapshot = Snapshot(aborted: [8]);
        Visibility.IsVisible(7, 0, in snapshot, Self).Should().BeTrue();
        Visibility.IsVisible(8, 0, in snapshot, Self).Should().BeFalse();
    }

    [Fact]
    public void Future_or_active_writer_xmin_is_invisible()
    {
        SnapshotState snapshot = Snapshot(activeWriter: 15);
        Visibility.IsVisible(21, 0, in snapshot, Self).Should().BeFalse();
        Visibility.IsVisible(15, 0, in snapshot, Self).Should().BeFalse();
    }

    [Fact]
    public void Committed_xmax_hides_record()
    {
        SnapshotState snapshot = Snapshot();
        Visibility.IsVisible(5, 9, in snapshot, Self).Should().BeFalse();
    }

    [Fact]
    public void Aborted_future_or_active_writer_xmax_does_not_hide_record()
    {
        SnapshotState snapshot = Snapshot(aborted: [9], activeWriter: 15);
        Visibility.IsVisible(5, 9, in snapshot, Self).Should().BeTrue();
        Visibility.IsVisible(5, 21, in snapshot, Self).Should().BeTrue();
        Visibility.IsVisible(5, 15, in snapshot, Self).Should().BeTrue();
    }

    [Fact]
    public void Bootstrap_snapshot_only_accepts_live_records()
    {
        SnapshotState snapshot = Snapshot(long.MaxValue);
        Visibility.IsVisible(100, 0, in snapshot, TransactionId.Bootstrap).Should().BeTrue();
        Visibility.IsVisible(0, 0, in snapshot, TransactionId.Bootstrap).Should().BeFalse();
        Visibility.IsVisible(100, 5, in snapshot, TransactionId.Bootstrap).Should().BeFalse();
    }
}

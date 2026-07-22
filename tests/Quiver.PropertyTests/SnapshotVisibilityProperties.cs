using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Quiver.Core;

namespace Quiver.PropertyTests;

public class SnapshotVisibilityProperties
{
    private const long HighWater = 90;
    private const long SelfTxId = 100;
    private static readonly TransactionId Self = new(SelfTxId);

    public sealed record VisibilityCase(long Xmin, long Xmax, long[] AbortedGaps);

    public static Arbitrary<VisibilityCase> Arb()
    {
        Gen<long> id = Gen.Choose(0, 120).Select(static value => (long)value);
        Gen<long[]> gaps = Gen.Sized(size =>
            id.ListOf(Math.Min(size, 8)).Select(static values => values.Distinct().ToArray()));
        return id.SelectMany(xmin =>
            id.SelectMany(xmax =>
                gaps.Select(aborted => new VisibilityCase(xmin, xmax, aborted))))
            .ToArbitrary();
    }

    private static SnapshotState Snapshot(VisibilityCase c)
        => new(HighWater, new HashSet<long>(c.AbortedGaps));

    [Property(MaxTest = 1000, Arbitrary = [typeof(SnapshotVisibilityProperties)])]
    public Property Zero_xmin_is_always_invisible(VisibilityCase c)
    {
        SnapshotState snapshot = Snapshot(c);
        return (!Visibility.IsVisible(0, c.Xmax, in snapshot, Self)).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(SnapshotVisibilityProperties)])]
    public Property Own_live_write_is_visible(VisibilityCase c)
    {
        SnapshotState snapshot = Snapshot(c);
        return Visibility.IsVisible(SelfTxId, 0, in snapshot, Self).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(SnapshotVisibilityProperties)])]
    public Property Own_delete_is_invisible(VisibilityCase c)
    {
        SnapshotState snapshot = Snapshot(c);
        return (!Visibility.IsVisible(SelfTxId, SelfTxId, in snapshot, Self)).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(SnapshotVisibilityProperties)])]
    public Property Future_xmin_is_invisible(VisibilityCase c)
    {
        SnapshotState snapshot = Snapshot(c);
        long future = HighWater + 1 + Math.Abs(c.Xmin % 9);
        return (!Visibility.IsVisible(future, 0, in snapshot, Self)).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(SnapshotVisibilityProperties)])]
    public Property Aborted_xmin_is_invisible(VisibilityCase c)
    {
        long xmin = 1 + Math.Abs(c.Xmin % (HighWater - 1));
        var gaps = new HashSet<long>(c.AbortedGaps) { xmin };
        var snapshot = new SnapshotState(HighWater, gaps);
        return (!Visibility.IsVisible(xmin, 0, in snapshot, Self)).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(SnapshotVisibilityProperties)])]
    public Property Aborted_xmax_does_not_hide_visible_record(VisibilityCase c)
    {
        long xmax = 2 + Math.Abs(c.Xmax % (HighWater - 2));
        var gaps = new HashSet<long>(c.AbortedGaps) { xmax };
        gaps.Remove(1);
        var snapshot = new SnapshotState(HighWater, gaps);
        return Visibility.IsVisible(1, xmax, in snapshot, Self).ToProperty();
    }
}

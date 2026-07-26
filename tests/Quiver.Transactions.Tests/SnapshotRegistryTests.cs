using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Transactions.Tests;

public sealed class SnapshotRegistryTests
{
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

using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Maintenance;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

[Collection("binary-backend-maintenance")]
public sealed class VacuumBudgetTests
{
    public static IEnumerable<object[]> Boundaries()
    {
        foreach (bool dryRun in new[] { false, true })
        for (int completed = 0; completed <= 7; completed++) yield return [dryRun, completed];
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Budget_stops_at_phase_boundary_and_remaining_work_survives_reopen(bool dryRun, int completed)
        => Verify(dryRun, completed, 5);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    public void Unlimited_or_sufficient_budget_completes_all_phases(int maximum) => Verify(false, 4, maximum);

    private static void Verify(bool dryRun, int completed, int maximum)
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_budget_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        try
        {
            VertexId live;
            VacuumReport result;
            using (var db = YatagarasuDatabase.Open(path))
            {
                using (var tx = db.BeginWriteTransaction())
                {
                    live = tx.CreateVertex("Live");
                    var b = tx.CreateVertex("B");
                    var owner = tx.CreateVertex("Owner");
                    var edge = tx.CreateEdge(live, b, "Link");
                    var nexus = tx.CreateNexus("Fact", [new("a", live), new("b", b)]);
                    tx.SetProperty(live, "value", PropertyValue.FromInt32(42));
                    tx.SetProperty(owner, "value", PropertyValue.FromFloatArray(new float[] { 1, 2, 3 }));
                    tx.SetProperty(edge, "value", PropertyValue.FromInt32(1));
                    tx.SetProperty(nexus, "value", PropertyValue.FromInt32(1));
                    tx.DeleteEdge(edge);
                    tx.DeleteNexus(nexus);
                    tx.DeleteVertex(owner);
                    tx.Commit();
                }
                ((BinaryGraphStorageBackend)db.BackendInternal).VacuumTimeProvider = new BoundaryClock(completed);
                result = db.Vacuum(new VacuumOptions
                {
                    Mode = dryRun ? VacuumMode.DryRun : VacuumMode.Full,
                    MaxDurationMs = maximum,
                });
                result.ReclaimedProperties.Should().Be(completed >= 1 ? 3 : 0);
                result.ReclaimedEdges.Should().Be(completed >= 2 ? 1 : 0);
                result.ReclaimedVertices.Should().Be(completed >= 3 ? 1 : 0);
                result.ReclaimedNexuses.Should().Be(completed >= 4 ? 1 : 0);
                result.ReclaimedIncidences.Should().Be(completed >= 4 ? 2 : 0);
                if (maximum == 5)
                    result.ElapsedMs.Should().Be(dryRun && completed == 7 ? 0 : 5);
            }
            using var reopened = YatagarasuDatabase.Open(path);
            using (var reader = reopened.BeginReadTransaction())
                reader.GetProperty(live, "value").Int32Value.Should().Be(42);
            var remainder = reopened.Vacuum();
            remainder.ReclaimedProperties.Should().Be(3 - (dryRun ? 0 : result.ReclaimedProperties));
            remainder.ReclaimedEdges.Should().Be(1 - (dryRun ? 0 : result.ReclaimedEdges));
            remainder.ReclaimedVertices.Should().Be(1 - (dryRun ? 0 : result.ReclaimedVertices));
            remainder.ReclaimedNexuses.Should().Be(1 - (dryRun ? 0 : result.ReclaimedNexuses));
            remainder.ReclaimedIncidences.Should().Be(2 - (dryRun ? 0 : result.ReclaimedIncidences));
            reopened.Vacuum().ReclaimedProperties.Should().Be(0);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class BoundaryClock(int completed) : TimeProvider
    {
        private int _calls;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _calls++ <= completed ? 0 : 5;
    }
}

using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// Nexusの運用統計と、二方向 incidence chain の整合性診断を検証する。
/// </summary>
public sealed class NexusDiagnosticsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public NexusDiagnosticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_nexus_diagnostics_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Statistics_report_live_nexuses_and_physical_incidences()
    {
        using var db = QuiverDatabase.Open(_path);
        NexusId nexusId;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateVertex("Entity");
            var b = tx.CreateVertex("Entity");
            nexusId = tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
            tx.Commit();
        }

        var created = db.Diagnostics.GetStatistics();
        created.NexusCount.Should().Be(1);
        created.IncidenceCount.Should().Be(2);

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNexus(nexusId);
            tx.Commit();
        }

        var deleted = db.Diagnostics.GetStatistics();
        deleted.NexusCount.Should().Be(0);
        deleted.IncidenceCount.Should().Be(2,
            "incidence slots remain physical until vacuum unlinks and frees them");
        db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue(
            "incidences owned by a logically deleted header are pending vacuum, not corruption");

        db.Vacuum(new Maintenance.VacuumOptions
        {
            Targets = Maintenance.VacuumTarget.Nexuses,
        });

        var vacuumed = db.Diagnostics.GetStatistics();
        vacuumed.NexusCount.Should().Be(0);
        vacuumed.IncidenceCount.Should().Be(0);
    }

    [Fact]
    public void CheckConsistency_returns_no_issues_for_valid_database()
    {
        using var db = QuiverDatabase.Open(_path);
        CreateFact(db);

        var report = db.Diagnostics.CheckConsistency();

        report.IsConsistent.Should().BeTrue();
        report.Issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckConsistency_detects_short_arity_and_nexus_unreachable_incidence()
    {
        using var db = QuiverDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;

        var first = backend.IncidenceStoreForTest.Write(graph.First);
        first.NextInNexus = IncidenceId.Invalid;
        first.Dispose();

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue => issue.Contains("arity 1", StringComparison.Ordinal));
        report.Issues.Should().Contain(issue =>
            issue.Contains($"Incidence {graph.Second.Sequence}", StringComparison.Ordinal)
            && issue.Contains("unreachable from its nexus", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckConsistency_detects_invalid_incidence_references()
    {
        using var db = QuiverDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;

        var first = backend.IncidenceStoreForTest.Write(graph.First);
        first.NexusId = NexusId.Invalid;
        first.VertexId = VertexId.Invalid;
        first.RoleId = RoleId.Invalid;
        first.Dispose();

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue => issue.Contains("invalid nexus", StringComparison.Ordinal));
        report.Issues.Should().Contain(issue => issue.Contains("invalid vertex", StringComparison.Ordinal));
        report.Issues.Should().Contain(issue => issue.Contains("invalid role", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckConsistency_detects_cycles_in_both_chain_directions()
    {
        using var db = QuiverDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;

        var first = backend.IncidenceStoreForTest.Write(graph.First);
        first.NextInNexus = graph.First;
        first.NextInVertex = graph.First;
        first.Dispose();

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue =>
            issue.Contains("Nexus", StringComparison.Ordinal)
            && issue.Contains("cycle", StringComparison.Ordinal));
        report.Issues.Should().Contain(issue =>
            issue.Contains("Vertex", StringComparison.Ordinal)
            && issue.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckConsistency_detects_vertex_unreachable_incidence()
    {
        using var db = QuiverDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;

        backend.VertexIncidenceHeadStoreForTest.Set(graph.FirstVertex, IncidenceId.Invalid);

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue =>
            issue.Contains($"Incidence {graph.First.Sequence}", StringComparison.Ordinal)
            && issue.Contains("unreachable from its vertex", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckConsistency_detects_duplicate_role_and_vertex_pair()
    {
        using var db = QuiverDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;
        using var original = backend.IncidenceStoreForTest.Read(graph.First);

        var second = backend.IncidenceStoreForTest.Write(graph.Second);
        second.VertexId = original.VertexId;
        second.RoleId = original.RoleId;
        second.Dispose();

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue =>
            issue.Contains("duplicate role and vertex pair", StringComparison.Ordinal));
    }

    private static FactGraph CreateFact(QuiverDatabase db)
    {
        NexusId nexusId;
        VertexId firstVertex;
        using (var tx = db.BeginTransaction())
        {
            firstVertex = tx.CreateVertex("Entity");
            var secondVertex = tx.CreateVertex("Entity");
            nexusId = tx.CreateNexus("Fact", [
                new("Subject", firstVertex),
                new("Object", secondVertex),
            ]);
            tx.Commit();
        }

        var backend = (BinaryGraphStorageBackend)db.BackendInternal;
        using var header = backend.NexusStoreForTest.Read(nexusId);
        IncidenceId first = header.FirstIncidenceId;
        using var firstRecord = backend.IncidenceStoreForTest.Read(first);
        return new FactGraph(firstVertex, first, firstRecord.NextInNexus);
    }

    private readonly record struct FactGraph(
        VertexId FirstVertex,
        IncidenceId First,
        IncidenceId Second);
}

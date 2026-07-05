using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ハイパーエッジの運用統計と、二方向 incidence chain の整合性診断を検証する。
/// </summary>
public sealed class HyperedgeDiagnosticsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public HyperedgeDiagnosticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hyperedge_diagnostics_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Statistics_report_live_hyperedges_and_physical_incidences()
    {
        using var db = GraphDatabase.Open(_path);
        HyperedgeId hyperedgeId;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("Entity");
            var b = tx.CreateNode("Entity");
            hyperedgeId = tx.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
            tx.Commit();
        }

        var created = db.Diagnostics.GetStatistics();
        created.HyperedgeCount.Should().Be(1);
        created.IncidenceCount.Should().Be(2);

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteHyperedge(hyperedgeId);
            tx.Commit();
        }

        var deleted = db.Diagnostics.GetStatistics();
        deleted.HyperedgeCount.Should().Be(0);
        deleted.IncidenceCount.Should().Be(2,
            "incidence slots remain physical until vacuum unlinks and frees them");
        db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue(
            "incidences owned by a logically deleted header are pending vacuum, not corruption");

        db.Vacuum(new Maintenance.VacuumOptions
        {
            Targets = Maintenance.VacuumTarget.Hyperedges,
        });

        var vacuumed = db.Diagnostics.GetStatistics();
        vacuumed.HyperedgeCount.Should().Be(0);
        vacuumed.IncidenceCount.Should().Be(0);
    }

    [Fact]
    public void CheckConsistency_returns_no_issues_for_valid_database()
    {
        using var db = GraphDatabase.Open(_path);
        CreateFact(db);

        var report = db.Diagnostics.CheckConsistency();

        report.IsConsistent.Should().BeTrue();
        report.Issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckConsistency_detects_short_arity_and_hyperedge_unreachable_incidence()
    {
        using var db = GraphDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;

        var first = backend.IncidenceStoreForTest.Write(graph.First);
        first.NextInHyperedge = IncidenceId.Invalid;
        first.Dispose();

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue => issue.Contains("arity 1", StringComparison.Ordinal));
        report.Issues.Should().Contain(issue =>
            issue.Contains($"Incidence {graph.Second.Sequence}", StringComparison.Ordinal)
            && issue.Contains("unreachable from its hyperedge", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckConsistency_detects_invalid_incidence_references()
    {
        using var db = GraphDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;

        var first = backend.IncidenceStoreForTest.Write(graph.First);
        first.HyperedgeId = HyperedgeId.Invalid;
        first.NodeId = NodeId.Invalid;
        first.RoleId = RoleId.Invalid;
        first.Dispose();

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue => issue.Contains("invalid hyperedge", StringComparison.Ordinal));
        report.Issues.Should().Contain(issue => issue.Contains("invalid node", StringComparison.Ordinal));
        report.Issues.Should().Contain(issue => issue.Contains("invalid role", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckConsistency_detects_cycles_in_both_chain_directions()
    {
        using var db = GraphDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;

        var first = backend.IncidenceStoreForTest.Write(graph.First);
        first.NextInHyperedge = graph.First;
        first.NextInNode = graph.First;
        first.Dispose();

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue =>
            issue.Contains("Hyperedge", StringComparison.Ordinal)
            && issue.Contains("cycle", StringComparison.Ordinal));
        report.Issues.Should().Contain(issue =>
            issue.Contains("Node", StringComparison.Ordinal)
            && issue.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckConsistency_detects_node_unreachable_incidence()
    {
        using var db = GraphDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;

        backend.NodeIncidenceHeadStoreForTest.Set(graph.FirstNode, IncidenceId.Invalid);

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue =>
            issue.Contains($"Incidence {graph.First.Sequence}", StringComparison.Ordinal)
            && issue.Contains("unreachable from its node", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckConsistency_detects_duplicate_role_and_node_pair()
    {
        using var db = GraphDatabase.Open(_path);
        var graph = CreateFact(db);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;
        using var original = backend.IncidenceStoreForTest.Read(graph.First);

        var second = backend.IncidenceStoreForTest.Write(graph.Second);
        second.NodeId = original.NodeId;
        second.RoleId = original.RoleId;
        second.Dispose();

        var report = db.Diagnostics.CheckConsistency();

        report.Issues.Should().Contain(issue =>
            issue.Contains("duplicate role and node pair", StringComparison.Ordinal));
    }

    private static FactGraph CreateFact(GraphDatabase db)
    {
        HyperedgeId hyperedgeId;
        NodeId firstNode;
        using (var tx = db.BeginTransaction())
        {
            firstNode = tx.CreateNode("Entity");
            var secondNode = tx.CreateNode("Entity");
            hyperedgeId = tx.CreateHyperedge("Fact", [
                new("Subject", firstNode),
                new("Object", secondNode),
            ]);
            tx.Commit();
        }

        var backend = (BinaryGraphStorageBackend)db.BackendInternal;
        using var header = backend.HyperedgeStoreForTest.Read(hyperedgeId);
        IncidenceId first = header.FirstIncidenceId;
        using var firstRecord = backend.IncidenceStoreForTest.Read(first);
        return new FactGraph(firstNode, first, firstRecord.NextInHyperedge);
    }

    private readonly record struct FactGraph(
        NodeId FirstNode,
        IncidenceId First,
        IncidenceId Second);
}

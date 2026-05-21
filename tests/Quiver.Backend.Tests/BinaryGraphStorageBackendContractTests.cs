using FluentAssertions;
using Quiver.Core;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// Runs the BA-2 backend contract suite against the default binary backend.
/// When new backends are added (e.g. SQLite in BA-5), add a sibling subclass
/// that returns the matching factory.
/// </summary>
public sealed class BinaryGraphStorageBackendContractTests : GraphStorageBackendContractTests
{
    protected override IGraphStorageBackendFactory CreateFactory()
        => new BinaryGraphStorageBackendFactory();

    /// <summary>
    /// FT-15 Tier1: rollback must roll back page-backed store metadata (the node
    /// store high-water mark), so the slot freed by the aborted CreateNode is
    /// reused by the next committed transaction. This is a binary-specific
    /// guarantee — id assignment is backend-defined — so it lives here rather
    /// than in the shared contract suite.
    /// </summary>
    [Fact]
    public void Rollback_rolls_back_store_high_water_mark()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_ft15_hwm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new BinaryGraphStorageBackendFactory();
            using var backend = factory.Open(dir, new GraphDatabaseOptions());

            NodeId discarded;
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                discarded = tx.CreateNode("Discarded");
                tx.Rollback();
            }

            NodeId reused;
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                reused = tx.CreateNode("Fresh");
                tx.Commit();
            }

            reused.Should().Be(discarded,
                "rollback must restore the node store high-water mark so the id is reused");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
